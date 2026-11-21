using System.Text.Json.Serialization;
using Core.Values;

namespace Core.Usage;

/// <summary>连不上上游时的分类。库里、JSON 里存的是每个取值标注的那串字符（见 <see cref="Wire{TEnum}"/>）。</summary>
[JsonConverter(typeof(WireEnumConverter<ConnectFailure>))]
public enum ConnectFailure
{
    /// <summary>域名解析失败。</summary>
    [JsonStringEnumMemberName("dns")] Dns,

    /// <summary>拒绝连接。</summary>
    [JsonStringEnumMemberName("refused")] Refused,

    /// <summary>TLS 握手失败。</summary>
    [JsonStringEnumMemberName("tls")] Tls,

    /// <summary>建连超时。</summary>
    [JsonStringEnumMemberName("connect_timeout")] ConnectTimeout,

    /// <summary>代理不可用。</summary>
    [JsonStringEnumMemberName("proxy")] Proxy,

    /// <summary>其他连接错误。</summary>
    [JsonStringEnumMemberName("other")] Other,
}
