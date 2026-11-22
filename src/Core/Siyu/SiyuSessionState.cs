namespace Core.Siyu;

/// <summary>思域会话的状态：只能 Active → Closing → Closed，Closed 不复活，重新启动必须新建会话。</summary>
public enum SiyuSessionState
{
    /// <summary>可以开隧道、准入发送。</summary>
    Active,

    /// <summary>已禁止新准入并取消在途，等隧道与发送许可归还。</summary>
    Closing,

    /// <summary>资源已全部归还；终态留一段时间供确认，之后查不到。</summary>
    Closed,
}
