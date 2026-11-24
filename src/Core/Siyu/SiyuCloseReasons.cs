namespace Core.Siyu;

/// <summary>会话结束原因的固定英文原因码与中文读句，唯一定义处；读句照设计的读屏句表，含下一步。</summary>
public static class SiyuCloseReasons
{
    /// <summary>固定原因码。</summary>
    public static string Code(SiyuCloseReason reason) => reason switch
    {
        SiyuCloseReason.Stopped => "stopped",
        SiyuCloseReason.TokenInvalid => "personal-token-invalid",
        SiyuCloseReason.AccountUnavailable => "official-account-unavailable",
        SiyuCloseReason.Maintenance => "site-maintenance",
        SiyuCloseReason.LeaseExpired => "lease-expired",
        SiyuCloseReason.ProfileChanged => "profile-changed",
        SiyuCloseReason.Reauthentication => "official-reauthentication-required",
        SiyuCloseReason.ServiceStopping => "service-stopping",
        SiyuCloseReason.AdministratorStopped => "stopped-by-administrator",
        SiyuCloseReason.AccountSwitched => "account-switched",
        SiyuCloseReason.IdentityMismatch => "identity-mismatch",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    /// <summary>读给使用者的中文句子。</summary>
    public static string Sentence(SiyuCloseReason reason) => reason switch
    {
        SiyuCloseReason.Stopped => "思域已停止。未确认完成的操作不会自动重发。",
        SiyuCloseReason.TokenInvalid => "思域的个人令牌已失效。请更新此启动配置的令牌，再启动。",
        SiyuCloseReason.AccountUnavailable => "这个官方账户在中转机上已不可用，思域已停止。请在 MyCoAI 改选别的账户。",
        SiyuCloseReason.Maintenance => "思域因设备维护已停止。维护完成后请重新启动，原会话不会自动换号。",
        SiyuCloseReason.LeaseExpired => "思域会话已结束，请重新启动；未确认完成的操作不会自动重发。",
        SiyuCloseReason.ProfileChanged => "这个官方账户的资料变了，思域已停止。请重新启动思域，让它带上新的资料。",
        SiyuCloseReason.Reauthentication => "此官方账户需要重新登录。请在中转机管理页重新登录该账户，再启动思域。",
        SiyuCloseReason.ServiceStopping => "中转服务已重启，思域会话已结束，请重新启动；未确认完成的操作不会自动重发。",
        SiyuCloseReason.AdministratorStopped => "管理员在中转机上结束了这个思域会话。需要时请重新启动思域；未确认完成的操作不会自动重发。",
        SiyuCloseReason.AccountSwitched => "中转机换了在用的上游，这个思域会话已结束。重新连接思域就会用上新的上游；未确认完成的操作不会自动重发。",
        SiyuCloseReason.IdentityMismatch => "这台电脑发来的请求身份和中转机给的不一致，思域会话已结束。重新连接思域会重新准备这台电脑的配置；再次不一致请检查是否有别的程序改了 Claude Code 的配置。",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}
