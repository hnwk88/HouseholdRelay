using System.Text.Json.Serialization;
using Core.Values;

namespace Core.Usage;

/// <summary>
/// 一次请求的结果；started 表示仍在处理或结束记录还没写入。库里、JSON 里存的是每个取值标注的那串字符（见 <see cref="Wire{TEnum}"/>）。
/// 声明顺序就是用量页「结果」下拉框的顺序（成功在前，处理中放最后）。
/// </summary>
[JsonConverter(typeof(WireEnumConverter<UsageOutcome>))]
public enum UsageOutcome
{
    /// <summary>成功。</summary>
    [JsonStringEnumMemberName("succeeded")] Succeeded,

    /// <summary>失败。</summary>
    [JsonStringEnumMemberName("failed")] Failed,

    /// <summary>客户端已取消。</summary>
    [JsonStringEnumMemberName("cancelled")] Cancelled,

    /// <summary>回传中断。</summary>
    [JsonStringEnumMemberName("interrupted")] Interrupted,

    /// <summary>处理中或结束记录还没写入。</summary>
    [JsonStringEnumMemberName("started")] Started,
}
