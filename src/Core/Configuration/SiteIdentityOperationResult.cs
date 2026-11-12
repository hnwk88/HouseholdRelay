namespace Core.Configuration;

/// <summary>维护操作自己的历史结果及站点当前快照；历史操作不保存另一份设备编号。</summary>
public sealed record SiteIdentityOperationResult(SiteIdentitySnapshot Current, string OperationId, long Version, bool Pending);
