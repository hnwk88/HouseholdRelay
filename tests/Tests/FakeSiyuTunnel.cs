using Application.Interfaces.Siyu;
using Core.Siyu;

namespace Tests;

/// <summary>假的思域隧道：只认固定个人令牌，可以手动关闭或标记为准入后失效，记下每次报告的状态码。</summary>
public sealed class FakeSiyuTunnel(OfficialHost host) : ISiyuTunnel
{
    /// <summary>客户端应出示的个人令牌。</summary>
    public const string PersonalToken = "hr_personal_token";

    /// <summary>发给官方的令牌。</summary>
    public const string OfficialToken = "sk-ant-oat01-official-token";

    private readonly CancellationTokenSource _closed = new();

    /// <summary>这条隧道的主机。</summary>
    public OfficialHost Host => host;

    /// <summary>为真时准入成功，但读完请求体后的再核对失败。</summary>
    public bool RevokeAfterAdmit { get; set; }

    /// <summary>每次准入的路由。</summary>
    public List<SiyuRoute> Admitted { get; } = [];

    /// <summary>每次报告的状态码。</summary>
    public List<int> Observed { get; } = [];

    /// <summary>许可报告过的发现（模板与编号），按报告顺序。</summary>
    public List<(byte[] Template, IReadOnlyList<string> Ids)> Discoveries { get; } = [];

    /// <summary>会话关闭令牌。</summary>
    public CancellationToken Closed => _closed.Token;

    /// <summary>模拟会话关闭。</summary>
    // 取消即模拟会话关闭，传输层据此关闭连接。
    public void Close() => _closed.Cancel();

    /// <summary>个人令牌不对抛 401 本地拒绝；否则记下路由并给许可。</summary>
    public Task<ISiyuPermit> AdmitAsync(SiyuRoute route, string bearerToken, CancellationToken cancellation)
    {
        if (bearerToken != PersonalToken) throw new SiyuRefusal(401, "personal-token", "思域的个人令牌已失效。请更新此启动配置的令牌，再启动。");
        // 准入可能并发发生，记录表加锁。
        lock (Admitted) Admitted.Add(route);
        var kind = route.Purpose switch { SiyuPurpose.Messages => Core.Usage.UsageKind.Inference, SiyuPurpose.CountTokens => Core.Usage.UsageKind.Estimate, SiyuPurpose.Models => Core.Usage.UsageKind.Models, _ => (Core.Usage.UsageKind?)null };
        var permit = new FakeSiyuPermit(this, kind is { } value ? new Core.Usage.UsageAccumulator(value) : null);
        // 许可可能并发发出，记录表加锁。
        lock (Permits) Permits.Add(permit);
        return Task.FromResult<ISiyuPermit>(permit);
    }

    /// <summary>发出过的许可，按准入顺序。</summary>
    public List<FakeSiyuPermit> Permits { get; } = [];

    /// <summary>再核对：标记了失效就抛 503 本地拒绝。</summary>
    public void ThrowIfRevoked()
    {
        if (RevokeAfterAdmit) throw new SiyuRefusal(503, "session-closing", "思域因设备维护已停止。维护完成后请重新启动，原会话不会自动换号。");
    }

    /// <summary>释放关闭令牌源。</summary>
    public void Dispose() => _closed.Dispose();
}
