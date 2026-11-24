namespace Core.Siyu;

/// <summary>
/// 版本 2 哪些原因码可以由 MyCoAI 自动处理、怎么处理，唯一定义处（协议文本「契约表:打回」与它逐项对照）。
/// 不在表里的码要人来处理：客户端停下、读出读句。打回只经控制面回应给出，代理入口的拒绝永远不带。
/// </summary>
public static class SiyuRemedies
{
    /// <summary>可自动处理的原因码与下一步。</summary>
    public static IReadOnlyDictionary<string, SiyuRemedyAction> Table { get; } = new Dictionary<string, SiyuRemedyAction>(StringComparer.Ordinal)
    {
        ["identity-version-stale"] = SiyuRemedyAction.Recreate,
        [SiyuCloseReasons.Code(SiyuCloseReason.ProfileChanged)] = SiyuRemedyAction.Recreate,
        [SiyuCloseReasons.Code(SiyuCloseReason.LeaseExpired)] = SiyuRemedyAction.Recreate,
        [SiyuCloseReasons.Code(SiyuCloseReason.ServiceStopping)] = SiyuRemedyAction.Recreate,
        [SiyuCloseReasons.Code(SiyuCloseReason.AdministratorStopped)] = SiyuRemedyAction.Recreate,
        [SiyuCloseReasons.Code(SiyuCloseReason.AccountSwitched)] = SiyuRemedyAction.Recreate,
        [SiyuCloseReasons.Code(SiyuCloseReason.IdentityMismatch)] = SiyuRemedyAction.Recreate,
        [SiyuCloseReasons.Code(SiyuCloseReason.AccountUnavailable)] = SiyuRemedyAction.Recreate,
        ["official-account-gone"] = SiyuRemedyAction.Recreate,
        ["session-not-found"] = SiyuRemedyAction.Recreate,
        [SiyuCloseReasons.Code(SiyuCloseReason.Maintenance)] = SiyuRemedyAction.Wait,
        ["access-database-unavailable"] = SiyuRemedyAction.Wait,
        ["usage-unavailable"] = SiyuRemedyAction.Wait,
    };

    /// <summary>这个原因码的打回；不在表里为 null。<paramref name="currentIdentityVersion"/> 只对身份代次过时有意义，作下一次建会话的期望代次。</summary>
    public static SiyuRemedy? For(string code, long? currentIdentityVersion) => Table.TryGetValue(code, out var action)
        ? action == SiyuRemedyAction.Wait ? new SiyuRemedy(action, RetryAfterSeconds: SiyuContract.RemedyWaitSeconds) : new SiyuRemedy(action, ExpectedIdentityVersion: currentIdentityVersion)
        : null;

    /// <summary>下一步的线上名字。</summary>
    public static string Wire(SiyuRemedyAction action) => action switch
    {
        SiyuRemedyAction.Recreate => "recreate",
        SiyuRemedyAction.Wait => "wait",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}
