using System.Text.Json.Serialization;
using Core.Values;

namespace Core.Upstream;

/// <summary>已保存上游的检测状态。库里、JSON 里存的是每个取值标注的那串字符（见 <see cref="Wire{TEnum}"/>）。</summary>
[JsonConverter(typeof(WireEnumConverter<CandidateState>))]
public enum CandidateState
{
    /// <summary>未测试（新保存或改了配置）。</summary>
    [JsonStringEnumMemberName("untested")] Untested,

    /// <summary>检测中。</summary>
    [JsonStringEnumMemberName("testing")] Testing,

    /// <summary>最小模型调用测试通过。</summary>
    [JsonStringEnumMemberName("passed")] Passed,

    /// <summary>测试没有通过。</summary>
    [JsonStringEnumMemberName("failed")] Failed,
}
