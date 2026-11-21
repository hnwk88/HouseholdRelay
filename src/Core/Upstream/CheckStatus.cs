using System.Text.Json.Serialization;
using Core.Values;

namespace Core.Upstream;

/// <summary>检测报告里每一项（连接、认证、模型目录、最小调用）的结论。库里、JSON 里存的是每个取值标注的那串字符（见 <see cref="Wire{TEnum}"/>）。</summary>
[JsonConverter(typeof(WireEnumConverter<CheckStatus>))]
public enum CheckStatus
{
    /// <summary>通过。</summary>
    [JsonStringEnumMemberName("passed")] Passed,

    /// <summary>失败。</summary>
    [JsonStringEnumMemberName("failed")] Failed,

    /// <summary>上游不提供模型目录。</summary>
    [JsonStringEnumMemberName("unsupported")] Unsupported,

    /// <summary>未检测。</summary>
    [JsonStringEnumMemberName("unverified")] Unverified,

    /// <summary>凭据失效，需重新接入。</summary>
    [JsonStringEnumMemberName("reconnect")] Reconnect,

    /// <summary>请求过多，稍后重试。</summary>
    [JsonStringEnumMemberName("cooldown")] Cooldown,

    /// <summary>检测已取消。</summary>
    [JsonStringEnumMemberName("cancelled")] Cancelled,
}
