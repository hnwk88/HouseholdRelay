using Application.Interfaces.Upstream;
using Core.Configuration;
using Core.Upstream;

namespace Application.Services.Upstream;

/// <summary>独立中转模式（不连数据库）的活动上游：由旧式配置、部署凭据和可选的本机模拟上游组成的固定快照。</summary>
public sealed class FixedUpstreamSource(UpstreamSnapshot snapshot) : IActiveUpstreamSource
{
    /// <summary>始终返回同一固定快照。</summary>
    public UpstreamSnapshot ReadActive() => snapshot;

    /// <summary>校验凭据格式并只允许本机 HTTP 模拟上游，再按旧式配置组成版本 0 的快照。</summary>
    public static FixedUpstreamSource ForLegacy(RelayConfiguration config, string? credential, Uri? testUpstream)
    {
        if (credential is not null && (string.IsNullOrWhiteSpace(credential) || credential.Any(char.IsControl))) throw new InvalidOperationException("账号令牌格式错误。");
        if (testUpstream is not null && !(testUpstream.Scheme == "http" && testUpstream.Host == "127.0.0.1"))
            throw new InvalidOperationException("模拟上游只允许本机 HTTP。");
        var options = UpstreamOptions.Legacy(config, testUpstream?.AbsoluteUri ?? "https://api.anthropic.com") with { AllowPrivateNetwork = testUpstream is not null };
        return new FixedUpstreamSource(new UpstreamSnapshot(0, options, credential));
    }
}
