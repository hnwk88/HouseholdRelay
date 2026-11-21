using System.Text.Json.Serialization;
using Core.Values;

namespace Core.Upstream;

/// <summary>凭据放在哪个请求头。库里、JSON 里存的是每个取值标注的那串字符（见 <see cref="Wire{TEnum}"/>）。</summary>
[JsonConverter(typeof(WireEnumConverter<AuthenticationScheme>))]
public enum AuthenticationScheme
{
    /// <summary>Authorization: Bearer。</summary>
    [JsonStringEnumMemberName("bearer")] Bearer,

    /// <summary>x-api-key 头。</summary>
    [JsonStringEnumMemberName("x-api-key")] XApiKey,
}
