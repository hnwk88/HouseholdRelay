namespace Application.Contracts.Upstream;

/// <summary>停用一个已保存上游时，仍按主键引用它的历史数据条数；这些数据照常保留，用量页照常读出它的名字。</summary>
/// <param name="Requests">用量记录条数。</param>
/// <param name="Attempts">上游尝试次数。</param>
/// <param name="Prices">单价条数。</param>
public sealed record UpstreamReferences(long Requests, long Attempts, long Prices);
