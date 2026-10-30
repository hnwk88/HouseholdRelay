using Core.Usage;

namespace Core.Upstream;

/// <summary>此刻的走法：最近一次接住请求的那一家与时刻，以及暂避、冷却、暂停中的各家；由连接池给出，上游页与概览按 <see cref="Describe"/> 读成一句话。</summary>
public sealed record UpstreamRouting(string? ServedId, DateTimeOffset? ServedAt, IReadOnlyList<UpstreamAvoidance> Held)
{
    /// <summary>
    /// 按读屏顺序读出「此刻实际走的是哪家；哪家暂避到几点、为什么」。开关关闭时只读在用的那一家；开着时在用的那家没被挡住就读它，
    /// 被挡住时读最近一次接住请求、且自己没被挡住的那一家，都没有时说下一个请求从备用上游开始。<paramref name="name"/> 按主键给出显示名称，时刻按本机时区读到秒。
    /// </summary>
    public string Describe(bool failover, string? activeId, Func<string?, string> name)
    {
        if (!failover) return $"自动切换已关闭：只用在用的「{name(activeId)}」，它失败时把错误直接交给客户端。";
        bool Blocked(string? id) => Held.Any(item => item.CandidateId == id);
        var now = !Blocked(activeId) ? $"此刻走的是在用的「{name(activeId)}」。"
            : ServedId is { } served && served != activeId && !Blocked(served) ? $"此刻走的是「{name(served)}」，{ServedAt?.ToLocalTime():HH:mm:ss} 接住了请求。"
            : "在用的那家暂时用不了，下一个请求按上游页的顺序从备用上游开始。";
        if (Held.Count == 0) return now + "没有上游在暂避。";
        return now + string.Concat(Held.Select(item => item.Kind switch
        {
            "paused" => $"「{name(item.CandidateId)}」认证失败已暂停，重新测试并切换后恢复。",
            "cooldown" => $"「{name(item.CandidateId)}」限流冷却到 {item.Until?.ToLocalTime():HH:mm:ss}。",
            _ => $"「{name(item.CandidateId)}」暂避到 {item.Until?.ToLocalTime():HH:mm:ss}，原因：{Reason(item)}。"
        }));
    }

    /// <summary>暂避原因：连不上时读连接失败分类，否则读「状态码，错误类型」；都没有时读「未知」。</summary>
    private static string Reason(UpstreamAvoidance item)
    {
        if (item.ErrorType == "connection_failed") return UsageLabels.ErrorType("connection_failed") + (item.ConnectFailure is { } failure ? "：" + UsageLabels.ConnectFailure(failure) : "");
        var parts = new[] { item.Status?.ToString(), item.ErrorType is { } type ? UsageLabels.ErrorType(type) : null }.Where(part => part is not null);
        return parts.Any() ? string.Join("，", parts) : "未知";
    }
}
