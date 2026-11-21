using System.Text.Json.Serialization;
using Core.Values;

namespace Core.Configuration;

/// <summary>全局出站代理的方式。库里、JSON 里存的是每个取值标注的那串字符（见 <see cref="Wire{TEnum}"/>）。</summary>
[JsonConverter(typeof(WireEnumConverter<ProxyMode>))]
public enum ProxyMode
{
    /// <summary>不使用代理，直连。</summary>
    [JsonStringEnumMemberName("none")] None,

    /// <summary>自动检测系统代理。</summary>
    [JsonStringEnumMemberName("auto")] Auto,

    /// <summary>手动指定代理地址。</summary>
    [JsonStringEnumMemberName("manual")] Manual,
}
