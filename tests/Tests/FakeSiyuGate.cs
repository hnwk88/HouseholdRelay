using Application.Interfaces.Siyu;
using Core.Siyu;

namespace Tests;

/// <summary>假的思域准入：只认一组会话编号与票据，每开一条隧道都记下来。</summary>
public sealed class FakeSiyuGate : ISiyuGate
{
    /// <summary>认可的会话编号。</summary>
    public const string SessionId = "session-1";

    /// <summary>认可的票据。</summary>
    public const string Ticket = "ticket-secret-1";

    /// <summary>开过的隧道。</summary>
    public List<FakeSiyuTunnel> Tunnels { get; } = [];

    /// <summary>新开的隧道在交出前先经过它（测试用来标记失效等）。</summary>
    public Action<FakeSiyuTunnel> Prepare { get; set; } = _ => { };

    /// <summary>编号和票据都对才开隧道。</summary>
    public ISiyuTunnel? OpenTunnel(string sessionId, string ticket, OfficialHost host)
    {
        if (sessionId != SessionId || ticket != Ticket) return null;
        var tunnel = new FakeSiyuTunnel(host);
        Prepare(tunnel);
        // 多条隧道可能同时打开，记录表加锁。
        lock (Tunnels) Tunnels.Add(tunnel);
        return tunnel;
    }
}
