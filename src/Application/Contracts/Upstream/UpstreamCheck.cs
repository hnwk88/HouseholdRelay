namespace Application.Contracts.Upstream;

/// <summary>
/// 一次后台检测的快照：检测编号（进程内递增，页面按它查状态、取消）、哪一家（已保存上游主键）、做哪件事、走到哪、读句（进行中时是「正在……」，结束后是结论或原因）。
/// 不含任何秘密。
/// </summary>
public sealed record UpstreamCheck(long Id, long UpstreamCandidateId, UpstreamCheckKind Kind, UpstreamCheckStatus Status, string Message)
{
    /// <summary>已经结束（不是进行中）。</summary>
    public bool Ended => Status != UpstreamCheckStatus.Running;
}
