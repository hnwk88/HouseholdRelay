using Application.Interfaces.Siyu;

namespace Tests;

/// <summary>假的发送许可：官方令牌固定，报告的状态码记到所属隧道。</summary>
public sealed class FakeSiyuPermit(FakeSiyuTunnel tunnel) : ISiyuPermit
{
    /// <summary>固定的假官方令牌。</summary>
    public string AccessToken => FakeSiyuTunnel.OfficialToken;

    /// <summary>跟着隧道关闭。</summary>
    public CancellationToken Cancellation => tunnel.Closed;

    /// <summary>隧道被标记为失效时抛本地拒绝。</summary>
    public void Recheck() => tunnel.ThrowIfRevoked();

    /// <summary>记下状态码。</summary>
    // 同一隧道上的许可可能在不同线程报告，记录表加锁。
    public void Observe(int status) { lock (tunnel.Observed) tunnel.Observed.Add(status); }

    /// <summary>记下报告的发现（模板与编号）。</summary>
    // 同一隧道上的许可可能在不同线程报告，记录表加锁。
    public void ReportDiscovery(byte[] requestTemplate, IReadOnlyList<string> connectorIds) { lock (tunnel.Discoveries) tunnel.Discoveries.Add((requestTemplate, connectorIds)); }

    /// <summary>归还什么也不做。</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
