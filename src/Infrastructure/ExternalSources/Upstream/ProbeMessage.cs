using System.Text;
using System.Text.Json;

namespace Infrastructure.ExternalSources.Upstream;

/// <summary>验证诊断响应的真实 Messages 结构及完整流事件，不用 HTTP 200 代替生成成功。</summary>
internal static class ProbeMessage
{
    /// <summary>校验一个 JSON 对象是否是真实的助手 Messages 响应（类型为 message、角色为 assistant、带非空 id 和 content 数组），通过时返回其中经安全检查的模型名，否则返回 null。</summary>
    internal static string? Model(JsonElement root, string secret)
    {
        if (root.ValueKind != JsonValueKind.Object || Text(root, "type") != "message" || Text(root, "role") != "assistant"
            || string.IsNullOrWhiteSpace(Text(root, "id")) || !root.TryGetProperty("content", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
            return null;
        var model = Text(root, "model");
        return model is not null && SafeModel(model, secret) ? model : null;
    }

    /// <summary>解析检测响应正文并返回模型名；非流式直接校验整段 JSON，流式则逐个事件检查，必须依次见到 message_start、非空文本增量、正常的 stop_reason 和 message_stop，且中途没有 error 事件，否则返回 null，表示检测未真正生成内容。</summary>
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

    /// <summary>判断上游返回的模型名能否安全保存和展示：长度 1 到 200、只含字母数字和 <c>-_./:</c>，且不含上游凭据片段，防止把秘密或注入内容写进记录。</summary>
    internal static bool SafeModel(string model, string secret) => model.Length is > 0 and <= 200
        && model.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':') && UpstreamResponses.Redact(model, secret) == model;
    /// <summary>读取对象上某个字符串属性；对象类型不符、属性缺失或不是字符串时返回 null，不抛异常。</summary>
    private static string? Text(JsonElement root, string property) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
