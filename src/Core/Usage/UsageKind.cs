using System.Text.Json.Serialization;
using Core.Values;

namespace Core.Usage;

/// <summary>接口类别：推理（/v1/messages）、估算（count_tokens）、模型查询（/v1/models）。库里、JSON 里存的是每个取值标注的那串字符（见 <see cref="Wire{TEnum}"/>）。</summary>
[JsonConverter(typeof(WireEnumConverter<UsageKind>))]
public enum UsageKind
{
    /// <summary>推理。</summary>
    [JsonStringEnumMemberName("inference")] Inference,

    /// <summary>估算。</summary>
    [JsonStringEnumMemberName("estimate")] Estimate,

    /// <summary>模型查询。</summary>
    [JsonStringEnumMemberName("models")] Models,
}
