using System.Text.Json.Serialization;
using Core.Values;

namespace Core.Upstream;

/// <summary>上游预设，按地址主机推出。库里、JSON 里存的是每个取值标注的那串字符（见 <see cref="Wire{TEnum}"/>）。</summary>
[JsonConverter(typeof(WireEnumConverter<UpstreamPreset>))]
public enum UpstreamPreset
{
    /// <summary>官方接口。</summary>
    [JsonStringEnumMemberName("anthropic")] Anthropic,

    /// <summary>兼容接口（中转）。</summary>
    [JsonStringEnumMemberName("compatible")] Compatible,
}
