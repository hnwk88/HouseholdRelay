using System.Text.Json.Serialization;
using Core.Values;

namespace Core.Usage;

/// <summary>计数（Token）的完整性。库里、JSON 里存的是每个取值标注的那串字符（见 <see cref="Wire{TEnum}"/>）。</summary>
[JsonConverter(typeof(WireEnumConverter<UsageCompleteness>))]
public enum UsageCompleteness
{
    /// <summary>完整。</summary>
    [JsonStringEnumMemberName("complete")] Complete,

    /// <summary>部分或不完整。</summary>
    [JsonStringEnumMemberName("partial")] Partial,

    /// <summary>上游没有提供。</summary>
    [JsonStringEnumMemberName("not_provided")] NotProvided,

    /// <summary>不适用（没发往上游或不是推理）。</summary>
    [JsonStringEnumMemberName("not_applicable")] NotApplicable,
}
