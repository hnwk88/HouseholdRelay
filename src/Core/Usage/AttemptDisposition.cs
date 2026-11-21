using System.Text.Json.Serialization;
using Core.Values;

namespace Core.Usage;

/// <summary>一次上游尝试的去向。库里、JSON 里存的是每个取值标注的那串字符（见 <see cref="Wire{TEnum}"/>）。</summary>
[JsonConverter(typeof(WireEnumConverter<AttemptDisposition>))]
public enum AttemptDisposition
{
    /// <summary>结果交给客户端。</summary>
    [JsonStringEnumMemberName("client")] Client,

    /// <summary>换下一家。</summary>
    [JsonStringEnumMemberName("next")] Next,
}
