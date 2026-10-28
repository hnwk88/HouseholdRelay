using System.Buffers;
using System.Text;
using System.Text.Json;

namespace HouseholdRelay;

/// <summary>收紧响应头，并在有界 JSON 或完整 SSE 事件内消除已知上游秘密。</summary>
internal static class UpstreamResponses
{
    private static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "request-id", "x-request-id", "retry-after", "x-should-retry", "anthropic-organization-id" };

    internal static async Task Copy(HttpResponseMessage upstream, HttpResponse response, string secret, string requestId, CancellationToken cancellation)
    {
        if (upstream.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new RelayFailure(502, "Unexpected upstream content encoding.");
        var excluded = upstream.Headers.Connection.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var header in upstream.Headers)
        {
            if (excluded.Contains(header.Key) || !(AllowedHeaders.Contains(header.Key) || header.Key.StartsWith("anthropic-ratelimit-", StringComparison.OrdinalIgnoreCase))) continue;
            var values = header.Value.ToArray();
            if (values.All(value => Redact(value, secret) == value)) response.Headers[header.Key] = values;
        }
        response.StatusCode = (int)upstream.StatusCode;
        response.Headers.CacheControl = "no-store"; response.Headers["X-Accel-Buffering"] = "no"; response.Headers["X-Relay-Request-Id"] = requestId;
        await using var stream = await upstream.Content.ReadAsStreamAsync(cancellation);
        if (upstream.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            response.ContentType = "text/event-stream; charset=utf-8";
            await CopyEvents(stream, response.Body, secret, cancellation);
        }
        else
        {
            var bytes = await ReadBounded(stream, upstream.IsSuccessStatusCode ? 16 * 1024 * 1024 : 1024 * 1024, cancellation);
            byte[] safe;
            try { safe = Json(bytes, secret); }
            catch (JsonException)
            {
                if (upstream.IsSuccessStatusCode) throw new RelayFailure(502, "Upstream returned an invalid protocol response.");
                safe = SafeError("Upstream returned an unreadable error response.");
            }
            response.ContentType = "application/json; charset=utf-8";
            await response.Body.WriteAsync(safe, cancellation);
        }
    }

    internal static async Task<byte[]> ReadBounded(Stream stream, int limit, CancellationToken cancellation)
    {
        using var memory = new MemoryStream(); var buffer = ArrayPool<byte>.Shared.Rent(32768);
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellation)) != 0)
            {
                if (memory.Length + count > limit) throw new RelayFailure(502, "Upstream response exceeds the supported size.");
                memory.Write(buffer, 0, count);
            }
            return memory.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }

    internal static string Redact(string value, string secret)
    {
        if (secret.Length == 0) return value;
        return value.Replace(secret, "[redacted]", StringComparison.Ordinal)
            .Replace(Uri.EscapeDataString(secret), "[redacted]", StringComparison.Ordinal)
            .Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), "[redacted]", StringComparison.Ordinal);
    }

    internal static byte[] Json(byte[] bytes, string secret)
    {
        using var document = JsonDocument.Parse(bytes);
        using var output = new MemoryStream(); using (var writer = new Utf8JsonWriter(output)) Write(document.RootElement, writer, secret);
        return output.ToArray();
    }

    private static void Write(JsonElement element, Utf8JsonWriter writer, string secret)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var item in element.EnumerateObject()) { writer.WritePropertyName(Redact(item.Name, secret)); Write(item.Value, writer, secret); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Write(item, writer, secret); writer.WriteEndArray(); break;
            case JsonValueKind.String: writer.WriteStringValue(Redact(element.GetString()!, secret)); break;
            default: element.WriteTo(writer); break;
        }
    }

    private static byte[] SafeError(string message) => JsonSerializer.SerializeToUtf8Bytes(new { type = "error", error = new { type = "api_error", message } });

    private static async Task CopyEvents(Stream input, Stream output, string secret, CancellationToken cancellation)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(32768);
        using var frame = new MemoryStream(); var lineStart = 0;
        try
        {
            int count;
            while ((count = await input.ReadAsync(buffer, cancellation)) != 0)
                for (var index = 0; index < count; index++)
                {
                    if (frame.Length >= 1024 * 1024) throw new RelayFailure(502, "Upstream stream event exceeds the supported size.");
                    frame.WriteByte(buffer[index]);
                    if (buffer[index] != 10) continue;
                    var length = (int)frame.Length - lineStart - 1;
                    if (length == 0 || length == 1 && frame.GetBuffer()[lineStart] == 13)
                    {
                        await FlushEvent(frame.ToArray(), output, secret, cancellation);
                        frame.SetLength(0); lineStart = 0;
                    }
                    else lineStart = (int)frame.Length;
                }
            if (frame.Length != 0) await FlushEvent(frame.ToArray(), output, secret, cancellation);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }

    private static async Task FlushEvent(byte[] frame, Stream output, string secret, CancellationToken cancellation)
    {
        var text = Encoding.UTF8.GetString(frame); var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var data = string.Join('\n', lines.Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line[5..].TrimStart(' ')));
        var isError = lines.Any(line => line.StartsWith("event:", StringComparison.Ordinal) && line[6..].Trim() == "error");
        if (data.Length != 0)
        {
            try
            {
                var safe = Encoding.UTF8.GetString(Json(Encoding.UTF8.GetBytes(data), secret));
                // 正常事件保留全部协议字段；解码字符串后检查，覆盖跨网络分片及 JSON 转义的秘密。
                var other = lines.Where(line => line.Length != 0 && !line.StartsWith("data:", StringComparison.Ordinal));
                text = string.Join('\n', other.Select(line => Redact(line, secret))) + "\ndata: " + safe + "\n\n";
                if (text.StartsWith('\n')) text = text[1..];
            }
            catch (JsonException)
            {
                text = isError ? "event: error\ndata: " + Encoding.UTF8.GetString(SafeError("Upstream returned an unreadable stream error.")) + "\n\n"
                    : Redact(text, secret);
            }
        }
        else text = Redact(text, secret);
        await output.WriteAsync(Encoding.UTF8.GetBytes(text), cancellation); await output.FlushAsync(cancellation);
    }
}
