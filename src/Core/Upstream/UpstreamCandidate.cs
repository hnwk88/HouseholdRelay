namespace Core.Upstream;

/// <summary>
/// 可向管理页面展示的已保存上游：主键（库生成的整数，关联一律走它）、对外编号 <see cref="Uid"/>（32 位小写十六进制，保存前生成；官方账户的账户目录按它命名，只交给官方分支）、
/// 供应商名称（只做显示）、保存时基于的活动版本、配置、检测状态、最近一次报告、此刻能不能删除（由仓储按删除时的同一判据给出，页面只读它）与导出搬走的时刻（没导出过为空）；不含任何秘密。
/// </summary>
public sealed record UpstreamCandidate(long Id, string Uid, string Name, long ExpectedVersion, UpstreamOptions Options, string State, UpstreamReport? Report, bool Deletable, DateTimeOffset? ExportedAt = null);
