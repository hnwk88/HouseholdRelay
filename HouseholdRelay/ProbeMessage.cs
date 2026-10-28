using System.Text;
using System.Text.Json;

namespace HouseholdRelay;

/// <summary>验证诊断响应的真实 Messages 结构及完整流事件，不用 HTTP 200 代替生成成功。</summary>
internal static class ProbeMessage
{
    internal static string? Model(JsonElement root, string secret)
    {
        if (root.ValueKind != JsonValueKind.Object || Text(root, "type") != "message" || Text(root, "role") != "assistant"
            || string.IsNullOrWhiteSpace(Text(root, "id")) || !root.TryGetProperty("content", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
            return null;
        var model = Text(root, "model");
        return model is not null && SafeModel(model, secret) ? model : null;
    }

    internal static string? Read(byte[] bytes, bool streaming, string secret)
    {
        if (!streaming) { using var document = JsonDocument.Parse(bytes); return Model(document.RootElement, secret); }
        string? model = null; var ended = false; var stopped = false; var generated = false;
        var data = new List<string>();
        bool Event()
        {
            if (data.Count == 0) return true;
            using var document = JsonDocument.Parse(string.Join('\n', data)); data.Clear();
            var root = document.RootElement; var type = Text(root, "type");
            if (type == "error" || stopped && type != "ping") return false;
            if (type == "message_start")
            {
                if (model is not null || !root.TryGetProperty("message", out var message)) return false;
                model = Model(message, secret); return model is not null;
            }
            if (type == "content_block_delta" && model is not null && root.TryGetProperty("delta", out var delta))
                generated |= Text(delta, "type") == "text_delta" && !string.IsNullOrEmpty(Text(delta, "text"));
            if (type == "message_delta" && model is not null && root.TryGetProperty("delta", out var finish))
                ended = Text(finish, "stop_reason") is "end_turn" or "max_tokens" or "stop_sequence";
            if (type == "message_stop") { if (model is null || !ended || !generated) return false; stopped = true; }
            return true;
        }
        foreach (var line in Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0) { if (!Event()) return null; }
            else if (line.StartsWith("data:", StringComparison.Ordinal)) data.Add(line[5..].TrimStart(' '));
        }
        return Event() && stopped ? model : null;
    }

    internal static bool SafeModel(string model, string secret) => model.Length is > 0 and <= 200
        && model.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':') && UpstreamResponses.Redact(model, secret) == model;
    private static string? Text(JsonElement root, string property) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
