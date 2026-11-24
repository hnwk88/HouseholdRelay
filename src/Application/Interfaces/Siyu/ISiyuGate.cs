using Core.Siyu;

namespace Application.Interfaces.Siyu;

/// <summary>思域代理入口的准入：外层 CONNECT 带的会话编号和票据换一条绑定了会话的隧道。传输层只认它，不自己查令牌、账户或站点身份。</summary>
public interface ISiyuGate
{
    /// <summary>
    /// 会话编号和票据对得上、会话处于可用状态、目标主机在会话许可内时返回隧道；对不上一律返回 null（传输层回 407，不说明是哪一项不对）。
    /// 例外：版本 2 会话名额满时抛出 <see cref="SiyuRefusal"/>（429 too-many-tunnels），传输层原样读出。票据只用于这次比较，不记日志。
    /// </summary>
    ISiyuTunnel? OpenTunnel(string sessionId, string ticket, OfficialHost host);
}
