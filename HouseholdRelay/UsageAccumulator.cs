using System.Text.Json;

namespace HouseholdRelay;

/// <summary>只保留计数和终止状态；SSE 解析使用响应层已经划分好的有界事件。</summary>
internal sealed class UsageAccumulator(string kind)
{
    private long? input, output, cacheRead, cacheWrite;
    private bool started, finalDelta, stopped, stream, jsonComplete;
    internal bool Error { get; private set; }
    internal bool IsStream => stream;
    internal bool ProtocolComplete => stream ? started && finalDelta && stopped && !Error : jsonComplete && !Error;
    internal UsageTokens Tokens => new(input, output, cacheRead, cacheWrite);
    internal UsageMoney? ReportedMoney { get; private set; }

    internal void Json(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        Error = Text(root, "type") == "error";
        if (kind == "estimate") input = Number(root, "input_tokens") ?? input;
        else if (kind == "inference" && root.TryGetProperty("usage", out var usage)) Update(usage);
        jsonComplete = true;
    }

    internal void Event(string eventName, string data, bool terminated)
    {
        stream = true;
        if (eventName == "error") Error = true;
        if (!terminated || data.Length == 0) return;
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            var type = Text(root, "type") ?? eventName;
            if (type == "error") Error = true;
            if (type == "message_start" && root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
            {
                started = true;
                if (message.TryGetProperty("usage", out var initial)) Update(initial);
            }
            if (type == "message_delta")
            {
                if (root.TryGetProperty("usage", out var usage)) Update(usage);
                if (root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object && Text(delta, "stop_reason") is not null) finalDelta = true;
            }
            if (type == "message_stop") stopped = true;
        }
        catch (JsonException) { /* 未知或非 JSON 扩展事件不影响转发，也不能补出用量。 */ }
    }

    internal string Completeness(bool dispatched, bool succeeded)
    {
        if (!dispatched || kind == "models") return "not_applicable";
        var any = input.HasValue || output.HasValue || cacheRead.HasValue || cacheWrite.HasValue;
        if (succeeded && ProtocolComplete && input.HasValue && (kind == "estimate" || output.HasValue)) return "complete";
        if (any || !succeeded || !ProtocolComplete) return "partial";
        return "not_provided";
    }

    private void Update(JsonElement usage)
    {
        if (kind != "inference" || usage.ValueKind != JsonValueKind.Object) return;
        input = Number(usage, "input_tokens") ?? input;
        output = Number(usage, "output_tokens") ?? output;
        cacheRead = Number(usage, "cache_read_input_tokens") ?? cacheRead;
        cacheWrite = Number(usage, "cache_creation_input_tokens") ?? cacheWrite;
        ReportedMoney = UsagePricing.ReadReported(usage) ?? ReportedMoney;
    }
    private static long? Number(JsonElement root, string key) => root.TryGetProperty(key, out var item)
        && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var value) && value >= 0 ? value : null;
    private static string? Text(JsonElement root, string key) => root.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
}
