using System.Security.Cryptography;
using System.Text;
using Application.Interfaces.Relay;

namespace Application.Services.Relay;

/// <summary>限时限量记录明确观测阶段；关闭时只检查状态，不扫描或复制请求正文。</summary>
public sealed class ProtocolDiagnostics(IRelayEventLog log, TimeProvider clock) : IProtocolDiagnostics
{
    private readonly object sync = new();
    private DateTimeOffset until;
    private int samples;
    private bool enabled;

    /// <summary>采集状态受同一锁保护，预算耗尽立即读作停止。</summary>
    public string Status
    {
        get
        {
            // 时间与样本预算在同一锁下判断，状态读取也会结束过期采集。
            lock (sync) return Active() ? $"临时取证进行中，已记录 {samples} 条，最多一百条或十分钟。" : "临时取证已关闭。";
        }
    }

    /// <summary>管理员明确开启后重置一次预算，不产生任何网络请求。</summary>
    public void Start()
    {
        // 开始和记录共用同一把锁，不会把旧采集样本计入新预算。
        lock (sync) { samples = 0; until = clock.GetUtcNow() + TimeSpan.FromMinutes(10); enabled = true; }
    }

    /// <summary>管理员关闭后后续记录立即停止。</summary>
    public void Stop()
    {
        // 与记录预算同步，停止返回后不会接受新样本。
        lock (sync) enabled = false;
    }

    /// <summary>先检查预算，再写白名单字段；无法确认的环境、目录与会话明确记为未知。</summary>
    public void Record(string correlation, string stage, string route, long generation, string deviceId,
        string? version = null, string? entrypoint = null, int? status = null, string? error = null)
    {
        // 锁覆盖限额判断和写入，最后一个样本不会被并发请求重复消费。
        lock (sync)
        {
            if (!Active()) return;
            samples++;
            log.Write(new
            {
                diagnostic = "site_identity", correlation = Hash(correlation), at = clock.GetUtcNow(),
                stage = stage is "assembled" or "dispatch" or "response" or "local_rejection" or "cli_completed" or "cli_failed" ? stage : "unknown",
                route = route is "/v1/messages" or "/v1/models" or "/v1/messages/count_tokens" or "cli" ? route : "unknown",
                generation, deviceHash = Hash(deviceId), version = Version(version),
                entrypoint = entrypoint is "cli" or "sdk-cli" ? entrypoint : "unknown",
                status, error = error is "local_rejection" or "authentication" or "forbidden" or "rate_limit" or "model_limit" or "verification" or "renewal_failed" ? error : "unknown",
                environment = "unknown", directory = "not_observed", session = "not_observed"
            });
        }
    }

    /// <summary>只允许数字版本格式；其他客户端提供的内容不能进入日志。</summary>
    private static string Version(string? value) => value is { Length: > 0 and < 40 } && value.All(c => char.IsAsciiDigit(c) || c == '.') ? value : "unknown";

    /// <summary>哈希仅用于本次有限样本关联，不保存原始标识。</summary>
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>调用方已持锁，任一预算达到即停止。</summary>
    private bool Active()
    {
        if (enabled && (samples >= 100 || clock.GetUtcNow() >= until)) enabled = false;
        return enabled;
    }
}
