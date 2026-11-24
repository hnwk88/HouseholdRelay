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

    /// <summary>（版本 2）后端此刻的选择不再是这个会话绑的那一家：换了在用的上游、官方与标准互换，或这一家不能再用于思域。</summary>
    AccountSwitched,

    /// <summary>（版本 2）这个会话收到过设备段或账户段不符、或没有可核对身份的 Messages 请求，续租时结束让客户端重新准备。</summary>
    IdentityMismatch,
}
