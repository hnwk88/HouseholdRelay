using Core.Upstream;

namespace Core.Siyu;

/// <summary>
/// 后端此刻给思域的选择（版本 2）：照上游页「在用」的那一家。在用的是能用于思域的官方账户 → 官方模式绑它（<see cref="Account"/>）；
/// 在用的是独立 API 或兼容中转 → 标准模式（<see cref="Account"/> 为 null，走旧接法，自动切换照旧）。
/// </summary>
public sealed record SiyuUpstreamChoice(SiyuMode Mode, UpstreamCandidateView? Account)
{
    /// <summary>能用于思域的官方账户判据，唯一定义处（建会话、账户列表、后端选择共用）：未损坏、官方登录、未导出、检测已通过。</summary>
    public static bool Usable(UpstreamCandidateView account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return !account.Damaged && account.Options.OfficialLogin && account.ExportedAt is null && account.State == CandidateState.Passed;
    }

    /// <summary>
    /// 由在用上游的概览与它来自的已保存上游（没有时为 null）推出选择；没配置、在用的是官方账户却不能用于思域（贴令牌的官方账户、没通过检测、已导出、损坏）时为 null。
    /// </summary>
    public static SiyuUpstreamChoice? Of(UpstreamOverview active, UpstreamCandidateView? source)
    {
        ArgumentNullException.ThrowIfNull(active);
        if (!active.Configured) return null;
        if (!active.Options.OfficialAccount) return new(SiyuMode.Standard, null);
        return source is not null && source.Id == active.UpstreamCandidateId && Usable(source) ? new(SiyuMode.Official, source) : null;
    }

    /// <summary>这个选择是不是一个会话已经绑定的那一家：模式相同，官方模式再要账户主键相同。</summary>
    public bool Same(SiyuMode mode, long? officialAccountId) => Mode == mode && (Mode == SiyuMode.Standard || Account!.Id == officialAccountId);
}
