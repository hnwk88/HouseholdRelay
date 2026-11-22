namespace Core.Upstream;

/// <summary>
/// Claude Code 实际调用检测没走完：本机找不到 claude.exe、子进程没有发出检测请求、或会话没有正常收尾。只有这一种读成「Claude Code 检测未完成」，
/// 别的 <see cref="InvalidOperationException"/>（地址规则拒绝、请求组装失败等）按它们自己的原因读出，不归到 Claude Code 头上。消息是给管理员的中文读句。
/// </summary>
public sealed class ClaudeCodeProbeIncompleteException : InvalidOperationException
{
    /// <summary>带读句。</summary>
    public ClaudeCodeProbeIncompleteException(string message) : base(message) { }

    /// <summary>带读句与原因。</summary>
    public ClaudeCodeProbeIncompleteException(string message, Exception inner) : base(message, inner) { }
}
