using Core.Upstream;

namespace Application.Interfaces.ClaudeCode;

/// <summary>发往官方账户的统一身份：按入口各取一份，取自中转所在电脑上真 claude.exe 用同一种模式实际发出的请求；本机 Claude Code 换了版本就自动重取。</summary>
public interface IClaudeCodeIdentitySource
{
    /// <summary>
    /// 返回入口 <paramref name="entrypoint"/>（取值见 <see cref="ClaudeCodeClient.CapturableEntrypoints"/>）与本机此刻这份 claude.exe 对得上的身份，需要时先在本机重取一次（不碰官方）。
    /// 找不到 Claude Code、取不到、取不全或对不上时抛 <see cref="ClaudeCodeIdentityUnavailableException"/>，消息是读给客户端和管理员的原因与下一步；调用方据此不发这个请求。
    /// </summary>
    Task<ClaudeCodeIdentity> Current(string entrypoint, CancellationToken cancellation);

    /// <summary>此刻身份的状态读成一句话，供上游页读出；不启动 Claude Code。</summary>
    string Describe();
}
