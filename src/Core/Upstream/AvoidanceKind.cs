using System.Text.Json.Serialization;
using Core.Values;

namespace Core.Upstream;

/// <summary>一家上游此刻为什么排不到前面。库里、JSON 里存的是每个取值标注的那串字符（见 <see cref="Wire{TEnum}"/>）。</summary>
[JsonConverter(typeof(WireEnumConverter<AvoidanceKind>))]
public enum AvoidanceKind
{
    /// <summary>换掉后暂避 5 分钟。</summary>
    [JsonStringEnumMemberName("avoid")] Avoid,

    /// <summary>429 的 Retry-After 冷却。</summary>
    [JsonStringEnumMemberName("cooldown")] Cooldown,

    /// <summary>401、403 认证暂停，重新测试并切换后恢复。</summary>
    [JsonStringEnumMemberName("paused")] Paused,
}
