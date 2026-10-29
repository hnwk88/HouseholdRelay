using System.Text.Json;

namespace Core.Usage;

/// <summary>只保留计数和终止状态；SSE 解析使用响应层已经划分好的有界事件。</summary>
public sealed class UsageAccumulator(string kind)
{
    private long? input, output, cacheRead, cacheWrite;
    private bool started, finalDelta, stopped, stream, jsonComplete;
    /// <summary>上游是否返回了错误：JSON 响应的 type 为 error，或流中出现 error 事件；一旦置为 true 就影响完整性判断。</summary>
    public bool Error { get; private set; }

    /// <summary>是否按 SSE 流处理过响应；只要收到过任何流事件即为 true。</summary>
    public bool IsStream => stream;

    /// <summary>
    /// 响应在协议层面是否完整结束：流式要求依次见到 message_start、带 stop_reason 的 message_delta 和 message_stop；
    /// 非流式要求整份 JSON 已解析。两者都要求没有出现错误。
    /// </summary>
    public bool ProtocolComplete => stream ? started && finalDelta && stopped && !Error : jsonComplete && !Error;

    /// <summary>目前累计到的各项计数快照；上游未报告的项为 null，而不是 0。</summary>
    public UsageTokens Tokens => new(input, output, cacheRead, cacheWrite);

    /// <summary>上游在 usage 中明确报告的金额（带币种）；未报告时为 null，后到的报告覆盖先到的。</summary>
    public UsageMoney? ReportedMoney { get; private set; }

    /// <summary>
    /// 处理一份完整的非流式 JSON 响应体：记录是否为错误；估算接口读取顶层 input_tokens，推理接口读取 usage 对象。
    /// 根不是对象时直接忽略且不标记为完整；内容不是合法 JSON 时会抛出 <see cref="JsonException"/>，由调用方保证传入已校验的 JSON。
    /// </summary>
    public void Json(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        Error = Text(root, "type") == "error";
        if (kind == "estimate") input = Number(root, "input_tokens") ?? input;
        else if (kind == "inference" && root.TryGetProperty("usage", out var usage)) Update(usage);
        jsonComplete = true;
    }

    /// <summary>
    /// 处理响应层切好的一个 SSE 事件：先标记为流式并记录 error 事件；只有以空行正常结束（<paramref name="terminated"/> 为 true）且有 data 的事件才解析，
    /// 从 message_start、message_delta、message_stop 中累计计数与结束标记。非 JSON 的 data 被忽略，不会抛出。每个请求一个实例，非线程安全。
    /// </summary>
    public void Event(string eventName, string data, bool terminated)
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

    /// <summary>
    /// 给出本次请求用量记录的完整性：未发往上游或模型查询为 <c>not_applicable</c>；成功、协议完整且有输入计数（推理还需输出计数）为 <c>complete</c>；
    /// 有任何计数、失败或协议不完整为 <c>partial</c>；其余（成功但上游未给任何计数）为 <c>not_provided</c>。
    /// </summary>
    public string Completeness(bool dispatched, bool succeeded)
    {
        if (!dispatched || kind == "models") return "not_applicable";
        var any = input.HasValue || output.HasValue || cacheRead.HasValue || cacheWrite.HasValue;
        if (succeeded && ProtocolComplete && input.HasValue && (kind == "estimate" || output.HasValue)) return "complete";
        if (any || !succeeded || !ProtocolComplete) return "partial";
        return "not_provided";
    }

    /// <summary>从推理接口的 usage 对象更新各项计数与上游报告金额；缺失或非法的字段保留原值，因此流中后到的部分 usage 不会清掉先到的计数。</summary>
    private void Update(JsonElement usage)
    {
        if (kind != "inference" || usage.ValueKind != JsonValueKind.Object) return;
        input = Number(usage, "input_tokens") ?? input;
        output = Number(usage, "output_tokens") ?? output;
        cacheRead = Number(usage, "cache_read_input_tokens") ?? cacheRead;
        cacheWrite = Number(usage, "cache_creation_input_tokens") ?? cacheWrite;
        ReportedMoney = UsageMoneyRules.ReadReported(usage) ?? ReportedMoney;
    }
    /// <summary>读取非负整数属性；属性缺失、不是数字、超出 64 位整数或为负数时返回 null。</summary>
    private static long? Number(JsonElement root, string key) => root.TryGetProperty(key, out var item)
        && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var value) && value >= 0 ? value : null;

    /// <summary>读取字符串属性；缺失或不是字符串时返回 null。</summary>
    private static string? Text(JsonElement root, string key) => root.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
}
