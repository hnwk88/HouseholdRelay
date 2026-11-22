namespace Core.Siyu;

/// <summary>思域会话为什么结束；原因码与读句见 <see cref="SiyuCloseReasons"/>。</summary>
public enum SiyuCloseReason
{
    /// <summary>使用者或 MyCoAI 主动停止。</summary>
    Stopped,

    /// <summary>个人令牌被撤销、轮换或使用者被停用。</summary>
    TokenInvalid,

    /// <summary>官方账户删除、搬走、停用或配置不再可用。</summary>
    AccountUnavailable,

    /// <summary>站点设备维护。</summary>
    Maintenance,

    /// <summary>租约到期没有续租。</summary>
    LeaseExpired,

    /// <summary>账户资料换代（身份或影响客户端分支的资料变了）。</summary>
    ProfileChanged,

    /// <summary>官方凭据过期且续期失败，需要重新登录。</summary>
    Reauthentication,

    /// <summary>中转服务退出。</summary>
    ServiceStopping,

    /// <summary>管理员在中转机管理页结束了这个会话。</summary>
    AdministratorStopped,
}
