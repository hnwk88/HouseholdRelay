namespace HouseholdRelay.Relay;

/// <summary>对外 HTTPS 中转宿主在本进程的启动状态；管理页只经它读，不认识中转宿主本身。</summary>
public interface IHttpsListener
{
    /// <summary>对外 HTTPS 中转宿主是否已在本进程启动。</summary>
    bool HttpsActive { get; }
}
