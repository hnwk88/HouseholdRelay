using System.Text.Json.Serialization;
using Core.Values;

namespace Core.Upstream;

/// <summary>上游凭据类型。库里、JSON 里存的是每个取值标注的那串字符（见 <see cref="Wire{TEnum}"/>）。</summary>
[JsonConverter(typeof(WireEnumConverter<UpstreamMode>))]
public enum UpstreamMode
{
    /// <summary>Claude 账户令牌（账户模式）。</summary>
    [JsonStringEnumMemberName("oauth")] OAuth,

    /// <summary>API 密钥。</summary>
    [JsonStringEnumMemberName("api-key")] ApiKey,
}
