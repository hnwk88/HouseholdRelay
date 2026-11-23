using Application.Interfaces.Siyu;
using Core.Usage;

namespace Tests;

/// <summary>假的发送许可：官方令牌固定，报告的状态码、发现、已发出与结束都记下来供比对。</summary>
public sealed class FakeSiyuPermit(FakeSiyuTunnel tunnel, UsageAccumulator? usage) : ISiyuPermit
{
    /// <summary>固定的假官方令牌。</summary>
    public string AccessToken => FakeSiyuTunnel.OfficialToken;

    /// <summary>跟着隧道关闭。</summary>
    public CancellationToken Cancellation => tunnel.Closed;

    /// <summary>按用途给的用量累加器。</summary>
    public UsageAccumulator? Usage => usage;

    /// <summary>已发出时交来的请求体；没发出为 null。</summary>
    public byte[]? DispatchedBody { get; private set; }

    /// <summary>结束报告：客户端状态码与是否完整；没报告为 null。</summary>
    public (int Status, bool Complete)? Completion { get; private set; }

    /// <summary>隧道被标记为失效时抛本地拒绝。</summary>
    public void Recheck() => tunnel.ThrowIfRevoked();

    /// <summary>记下状态码。</summary>
    // 同一隧道上的许可可能在不同线程报告，记录表加锁。
    public void Observe(int status) { lock (tunnel.Observed) tunnel.Observed.Add(status); }

    /// <summary>记下报告的发现（模板与编号）。</summary>
    // 同一隧道上的许可可能在不同线程报告，记录表加锁。
    public void ReportDiscovery(byte[] requestTemplate, IReadOnlyList<string> connectorIds) { lock (tunnel.Discoveries) tunnel.Discoveries.Add((requestTemplate, connectorIds)); }

    /// <summary>记下已发出的请求体。</summary>
    public Task DispatchingAsync(ReadOnlyMemory<byte> body, CancellationToken cancellation) { DispatchedBody = body.ToArray(); return Task.CompletedTask; }

    /// <summary>记下结束报告。</summary>
    public void Completed(int clientStatus, bool complete) => Completion = (clientStatus, complete);

    /// <summary>归还什么也不做。</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
