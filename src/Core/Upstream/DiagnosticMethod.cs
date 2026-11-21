using System.Text.Json.Serialization;
using Core.Values;

namespace Core.Upstream;

/// <summary>候选检测方式。库里、JSON 里存的是每个取值标注的那串字符（见 <see cref="Wire{TEnum}"/>）。</summary>
[JsonConverter(typeof(WireEnumConverter<DiagnosticMethod>))]
public enum DiagnosticMethod
{
    /// <summary>最小 Messages 调用。</summary>
    [JsonStringEnumMemberName("messages")] Messages,

    /// <summary>启动真实 Claude Code 会话验证。</summary>
    [JsonStringEnumMemberName("claude-code")] ClaudeCode,
}
