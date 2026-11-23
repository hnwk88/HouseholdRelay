using Application.Interfaces.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HouseholdRelay.Management;

/// <summary>就绪检查之一：库打得开、设置读得出来。读不出来时如实报「数据库打不开」，不带 SQL 或路径。</summary>
public sealed class DatabaseReadiness : IHealthCheck
{
    private readonly IConfigurationService _settings;

    /// <summary>读设置的服务必填，当场判空。</summary>
    public DatabaseReadiness(IConfigurationService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>读一次设置；成功为就绪，任何失败都读作「数据库打不开」，异常交给健康检查框架（它不把异常写进我们的响应）。</summary>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try { await _settings.ReadAsync(); return HealthCheckResult.Healthy("数据库可以打开。"); }
        // 库打不开、被占用或损坏：如实报未就绪；响应只写这句读句，异常细节不出本机进程。
        catch (Exception error) when (error is not OperationCanceledException) { return HealthCheckResult.Unhealthy("数据库打不开，请检查数据目录或恢复备份。", error); }
    }
}
