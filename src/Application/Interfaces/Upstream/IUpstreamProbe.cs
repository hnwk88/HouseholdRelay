using Application.Contracts.Upstream;
using Core.Upstream;

namespace Application.Interfaces.Upstream;

/// <summary>独立诊断候选配置：获取模型目录（顺带探测认证方式）、最小调用及 Claude Code 实际会话检测。</summary>
public interface IUpstreamProbe
{
    /// <summary>只读取模型目录，不发模型调用、不花额度；按认证顺序逐个尝试，返回被接受的认证方式与模型清单。</summary>
    Task<ModelCatalog> DiscoverAsync(UpstreamSnapshot snapshot, CancellationToken cancellation);

    /// <summary>对快照执行一次诊断，返回不含秘密的检测报告与实际采用的配置；不改变活动上游的暂停和冷却状态。</summary>
    Task<ProbeResult> RunAsync(UpstreamSnapshot snapshot, CancellationToken cancellation);

    /// <summary>
    /// 登录官方账户的上游：照原生登录的写法向官方取一次账户资料（GET /api/oauth/profile），返回其中的账户编号等三项；
    /// 被拒、限流、连接失败或资料形状不对时抛 <see cref="InvalidOperationException"/>，原因读给管理员、不含秘密。只在获取模型与测试时调用，正常转发不调用。
    /// </summary>
    Task<OfficialAccountProfile> ProfileAsync(UpstreamSnapshot snapshot, CancellationToken cancellation);
}
