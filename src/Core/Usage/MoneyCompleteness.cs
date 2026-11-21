using System.Text.Json.Serialization;
using Core.Values;

namespace Core.Usage;

/// <summary>金额的完整性。库里、JSON 里存的是每个取值标注的那串字符（见 <see cref="Wire{TEnum}"/>）。</summary>
[JsonConverter(typeof(WireEnumConverter<MoneyCompleteness>))]
public enum MoneyCompleteness
{
    /// <summary>完整。</summary>
    [JsonStringEnumMemberName("complete")] Complete,

    /// <summary>部分。</summary>
    [JsonStringEnumMemberName("partial")] Partial,

    /// <summary>不适用。</summary>
    [JsonStringEnumMemberName("not_applicable")] NotApplicable,

    /// <summary>未知。</summary>
    [JsonStringEnumMemberName("unknown")] Unknown,
}
