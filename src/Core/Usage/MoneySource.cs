using System.Text.Json.Serialization;
using Core.Values;

namespace Core.Usage;

/// <summary>金额从哪里来。库里、JSON 里存的是每个取值标注的那串字符（见 <see cref="Wire{TEnum}"/>）。</summary>
[JsonConverter(typeof(WireEnumConverter<MoneySource>))]
public enum MoneySource
{
    /// <summary>上游报告。</summary>
    [JsonStringEnumMemberName("reported")] Reported,

    /// <summary>按单价快照估算。</summary>
    [JsonStringEnumMemberName("estimated")] Estimated,

    /// <summary>不适用。</summary>
    [JsonStringEnumMemberName("not_applicable")] NotApplicable,

    /// <summary>未知（没有报告也没有单价）。</summary>
    [JsonStringEnumMemberName("unknown")] Unknown,
}
