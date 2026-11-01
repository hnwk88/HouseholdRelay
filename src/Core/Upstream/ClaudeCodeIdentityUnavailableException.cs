namespace Core.Upstream;

/// <summary>发往官方账户的请求取不到中转所在电脑 Claude Code 的统一身份时抛出：一律不发，绝不退回客户端标识或设置里的固定字符串；消息是读给客户端和管理员的原因与下一步。</summary>
public sealed class ClaudeCodeIdentityUnavailableException(string message) : InvalidOperationException(message);
