using Core.Upstream;

namespace Application.Contracts.Upstream;

/// <summary>一次候选测试的结果：不含秘密的检测报告，以及实际采用的配置（自动探测出的认证方式与检测方式已写进去，供服务写回候选）。</summary>
public sealed record ProbeResult(UpstreamReport Report, UpstreamOptions Used);
