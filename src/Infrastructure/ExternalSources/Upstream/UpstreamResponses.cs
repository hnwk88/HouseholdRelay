using System.Buffers;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Core.Relay;
using Core.Usage;
using Microsoft.AspNetCore.Http;

namespace Infrastructure.ExternalSources.Upstream;

/// <summary>收紧响应头，并在有界 JSON 或完整 SSE 事件内消除已知上游秘密。</summary>
public static class UpstreamResponses
{
    private static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "request-id", "x-request-id", "retry-after", "x-should-retry", "anthropic-organization-id" };

    /// <summary>把上游响应安全地转给家庭客户端：先按 Content-Encoding 解开压缩（见 <see cref="Decompress"/>），再做脱敏与计量，回给客户端的是解开后的明文；响应头只放行请求编号、重试和限流类白名单且值中不含秘密；附加禁止缓存、禁止代理缓冲和本次中转请求编号；SSE 逐事件脱敏转发，普通 JSON 在大小上限内（成功 16 MB、错误 1 MB）整体脱敏后写出；可选地把内容交给用量累加器统计。</summary>
    public static async Task Copy(HttpResponseMessage upstream, HttpResponse response, string secret, string requestId, CancellationToken cancellation, UsageAccumulator? usage = null)
    {
        var excluded = upstream.Headers.Connection.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var header in upstream.Headers)
        {
            if (excluded.Contains(header.Key) || !(AllowedHeaders.Contains(header.Key) || header.Key.StartsWith("anthropic-ratelimit-", StringComparison.OrdinalIgnoreCase))) continue;
            var values = header.Value.ToArray();
            if (values.All(value => Redact(value, secret) == value)) response.Headers[header.Key] = values;
        }
        response.StatusCode = (int)upstream.StatusCode;
        response.Headers.CacheControl = "no-store"; response.Headers["X-Accel-Buffering"] = "no"; response.Headers["X-Relay-Request-Id"] = requestId;
        await using var raw = await upstream.Content.ReadAsStreamAsync(cancellation);
        await using var stream = Decompress(raw, upstream.Content.Headers.ContentEncoding);
        if (upstream.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            response.ContentType = "text/event-stream; charset=utf-8";
            await CopyEvents(stream, response.Body, secret, cancellation, usage);
        }
        else
        {
            var bytes = await ReadBounded(stream, upstream.IsSuccessStatusCode ? 16 * 1024 * 1024 : 1024 * 1024, cancellation);
            byte[] safe; var unreadable = false;
            try { safe = Json(bytes, secret); }
            // 成功响应不是合法 JSON 说明协议异常，直接报 502；错误响应无法解析时换成固定错误正文，避免原文中的秘密被原样转出。
            catch (JsonException)
            {
                if (upstream.IsSuccessStatusCode) throw new RelayFailure(502, "Upstream returned an invalid protocol response.");
                safe = SafeError("Upstream returned an unreadable error response."); unreadable = true;
            }
            response.ContentType = "application/json; charset=utf-8";
            usage?.Json(safe);
            // 替换正文读出来是 api_error，要在它之后标明原文无法解析，尝试记录才知道真实情况。
            if (unreadable) usage?.Unreadable();
            await response.Body.WriteAsync(safe, cancellation);
        }
    }

    /// <summary>
    /// 自动切换要丢弃这家的错误响应时，只为尝试记录读出错误类型与说明：正文限 1 MB 读入，脱敏后交给 <paramref name="usage"/>，不是 JSON 时标为无法解析；
    /// 一个字节也不写给客户端。读正文途中连接断了或超限时放弃，错误类型留给尝试记录按「格式不认识」读；总超时或客户端断开的取消照常抛出。
    /// </summary>
    public static async Task Inspect(HttpResponseMessage upstream, string secret, UsageAccumulator usage, CancellationToken cancellation)
    {
        byte[] bytes;
        try { bytes = await ReadDecoded(upstream, 1024 * 1024, cancellation); }
        // 要丢弃的响应读不全也不影响换下一家，只是说不出它为什么失败。
        catch (Exception error) when (error is IOException or HttpRequestException or RelayFailure) { return; }
        try { usage.Json(Json(bytes, secret)); }
        // 和交给客户端时同一口径：非 JSON 的错误正文记为无法解析。
        catch (JsonException) { usage.Unreadable(); }
    }

    /// <summary>
    /// 按响应的 <c>Content-Encoding</c> 把上游响应流解开成明文，之后的脱敏与计量都在明文上做；回给家庭客户端的永远是解开后的内容（不透传压缩流）。
    /// 支持 identity（原样）、gzip、deflate（RFC 1950 zlib）、br、zstd（纯托管 ZstdSharp，无原生库）；这几种正是官方账户路径按客户端 Accept-Encoding 报给官方、官方可能回的编码。
    /// 多层或不认识的编码一律抛 502：发给官方的 Accept-Encoding 只含这里能解的几种，所以不会出现「报了却解不开」。解压结果仍受 <see cref="ReadBounded"/> 与 SSE 单事件上限约束，压缩炸弹在解开后按超限处理。
    /// </summary>
    private static Stream Decompress(Stream source, IEnumerable<string> encodings)
    {
        var applied = encodings.Where(value => !string.IsNullOrWhiteSpace(value) && !value.Equals("identity", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (applied.Length == 0) return source;
        if (applied.Length != 1) throw new RelayFailure(502, "Unexpected upstream content encoding.");
        return applied[0].Trim().ToLowerInvariant() switch
        {
            "gzip" or "x-gzip" => new GZipStream(source, CompressionMode.Decompress),
            "deflate" => new ZLibStream(source, CompressionMode.Decompress),
            "br" => new BrotliStream(source, CompressionMode.Decompress),
            "zstd" => new ZstdSharp.DecompressionStream(source),
            _ => throw new RelayFailure(502, "Unexpected upstream content encoding.")
        };
    }

    /// <summary>
    /// 中转自己要解析上游回复（测试的最小调用、模型目录、账户资料、丢弃前的错误检查）时用它取正文：按 <c>Content-Encoding</c> 解开（见 <see cref="Decompress"/>）后在 <paramref name="limit"/> 内读入明文。
    /// 压缩数据损坏与不认识的编码一样抛 502 的 <see cref="RelayFailure"/>，调用方按「不支持的响应」读出，不把解压库的异常漏给页面。
    /// </summary>
    public static async Task<byte[]> ReadDecoded(HttpResponseMessage upstream, int limit, CancellationToken cancellation)
    {
        await using var raw = await upstream.Content.ReadAsStreamAsync(cancellation);
        try { await using var stream = Decompress(raw, upstream.Content.Headers.ContentEncoding); return await ReadBounded(stream, limit, cancellation); }
        // 声称压缩却解不开：按协议异常处理，和多层或不认识的编码同一口径。
        catch (Exception error) when (error is InvalidDataException or ZstdSharp.ZstdException) { throw new RelayFailure(502, "Upstream returned a corrupt compressed response."); }
    }

    /// <summary>把流读入内存，累计超过 <paramref name="limit"/> 字节立即抛 502，防止异常大的上游响应耗尽内存；读缓冲取自共享数组池。</summary>
    public static async Task<byte[]> ReadBounded(Stream stream, int limit, CancellationToken cancellation)
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
        // 无论成功、超限还是取消都归还池化缓冲，并清零内容，避免响应数据残留给下一个借用者。
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }

    /// <summary>把文本中出现的上游凭据原文、URL 编码形式和 Base64 形式都替换为 <c>[redacted]</c>；凭据为空时原样返回。</summary>
    public static string Redact(string value, string secret)
    {
        if (secret.Length == 0) return value;
        return value.Replace(secret, "[redacted]", StringComparison.Ordinal)
            .Replace(Uri.EscapeDataString(secret), "[redacted]", StringComparison.Ordinal)
            .Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), "[redacted]", StringComparison.Ordinal);
    }

    /// <summary>解析 JSON 后逐个属性名和字符串值脱敏再重新序列化；因为先解码转义再比对，能发现被 JSON 转义藏起来的秘密。正文不是合法 JSON 时抛 <see cref="JsonException"/>。</summary>
    public static byte[] Json(byte[] bytes, string secret)
    {
        using var document = JsonDocument.Parse(bytes);
        using var output = new MemoryStream(); using (var writer = new Utf8JsonWriter(output)) Write(document.RootElement, writer, secret);
        return output.ToArray();
    }

    /// <summary>递归写出一个 JSON 元素：对象和数组逐项下钻，属性名与字符串值经过脱敏，数字、布尔和 null 原样写出。</summary>
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

    /// <summary>生成 Anthropic 格式的固定错误正文（api_error），在上游错误内容无法安全解析时替代原文返回。</summary>
    private static byte[] SafeError(string message) => JsonSerializer.SerializeToUtf8Bytes(new { type = "error", error = new { type = "api_error", message } });

    /// <summary>按空行切分 SSE 事件，每凑齐一个完整事件就脱敏转发并立即刷新；单个事件超过 1 MB 抛 502；流结束时残留的未终止片段也会处理。</summary>
    private static async Task CopyEvents(Stream input, Stream output, string secret, CancellationToken cancellation, UsageAccumulator? usage)
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
                        await FlushEvent(frame.ToArray(), output, secret, cancellation, usage, true);
                        frame.SetLength(0); lineStart = 0;
                    }
                    else lineStart = (int)frame.Length;
                }
            if (frame.Length != 0) await FlushEvent(frame.ToArray(), output, secret, cancellation, usage, false);
        }
        // 客户端断开、事件超限或取消时也要归还并清零池化缓冲，防止流内容残留或缓冲泄漏。
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }

    /// <summary>处理一个完整的 SSE 事件：先交给用量累加器，再把 data 部分按 JSON 脱敏重组、其余行按文本脱敏，写出后立即刷新以保持流式体验；<paramref name="terminated"/> 表示该事件是否以空行正常结束。</summary>
    private static async Task FlushEvent(byte[] frame, Stream output, string secret, CancellationToken cancellation, UsageAccumulator? usage, bool terminated)
    {
        var text = Encoding.UTF8.GetString(frame); var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var data = string.Join('\n', lines.Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line[5..].TrimStart(' ')));
        var isError = lines.Any(line => line.StartsWith("event:", StringComparison.Ordinal) && line[6..].Trim() == "error");
        var eventName = lines.FirstOrDefault(line => line.StartsWith("event:", StringComparison.Ordinal))?[6..].Trim() ?? "";
        usage?.Event(eventName, data, terminated);
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
            // data 不是合法 JSON：错误事件换成固定错误正文，避免把无法逐字段检查的原文转出；其他事件退回整段文本脱敏。
            catch (JsonException)
            {
                if (isError) usage?.Unreadable();
                text = isError ? "event: error\ndata: " + Encoding.UTF8.GetString(SafeError("Upstream returned an unreadable stream error.")) + "\n\n"
                    : Redact(text, secret);
            }
        }
        else text = Redact(text, secret);
        await output.WriteAsync(Encoding.UTF8.GetBytes(text), cancellation); await output.FlushAsync(cancellation);
    }
}
