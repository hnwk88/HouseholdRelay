namespace HouseholdRelay.Siyu;

/// <summary>思域代理在本进程是否真的起来了：只由 serve 在代理宿主启动成功后置为真；管理页据此读「已打开」还是「勾了但没起来」。</summary>
public sealed class SiyuHostState
{
    /// <summary>代理宿主已在本进程启动。</summary>
    public bool Active { get; internal set; }

    /// <summary>没起来时的原因（读给管理员听）；起来了或没开思域时为 null。</summary>
    public string? Problem { get; internal set; }
}
