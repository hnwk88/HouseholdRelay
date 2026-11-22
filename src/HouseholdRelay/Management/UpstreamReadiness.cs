using Application.Interfaces.Upstream;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HouseholdRelay.Management;

/// <summary>就绪检查之二：在用的上游已配置（与上游页「已配置」同一口径，都读上游概览的「已配置」）。没配置时如实报「还没有可用的上游」。</summary>
public sealed class UpstreamReadiness : IHealthCheck
{
    private readonly IUpstreamService _upstreams;

    /// <summary>上游服务必填，当场判空。</summary>
    public UpstreamReadiness(IUpstreamService upstreams)
    {
        ArgumentNullException.ThrowIfNull(upstreams);
        _upstreams = upstreams;
    }

    /// <summary>读一次上游概览：已配置为就绪，否则未就绪；读不出来（库打不开）也是未就绪。</summary>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return (await _upstreams.ReadOverview()).Configured ? HealthCheckResult.Healthy("在用的上游已配置。")
                : HealthCheckResult.Unhealthy("还没有可用的上游，请在本机管理页「上游接入」里添加并切换。");
        }
        // 读不出上游概览（库打不开、配置损坏）：如实报未就绪，读句不带细节。
        catch (Exception error) when (error is not OperationCanceledException) { return HealthCheckResult.Unhealthy("读不出在用的上游，请检查数据目录或上游配置。", error); }
    }
}
