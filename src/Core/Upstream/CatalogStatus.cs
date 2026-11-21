using System.Text.Json.Serialization;
using Core.Values;

namespace Core.Upstream;

/// <summary>「获取模型」的结论。库里、JSON 里存的是每个取值标注的那串字符（见 <see cref="Wire{TEnum}"/>）。</summary>
[JsonConverter(typeof(WireEnumConverter<CatalogStatus>))]
public enum CatalogStatus
{
    /// <summary>拿到了模型目录。</summary>
    [JsonStringEnumMemberName("passed")] Passed,

    /// <summary>上游不提供模型目录。</summary>
    [JsonStringEnumMemberName("unsupported")] Unsupported,

    /// <summary>可试的认证方式都被拒。</summary>
    [JsonStringEnumMemberName("rejected")] Rejected,

    /// <summary>上游限流。</summary>
    [JsonStringEnumMemberName("cooldown")] Cooldown,

    /// <summary>连接、协议或上游返回错误。</summary>
    [JsonStringEnumMemberName("failed")] Failed,
}
