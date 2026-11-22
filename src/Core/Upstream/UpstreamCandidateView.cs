namespace Core.Upstream;

/// <summary>
/// 可向管理页面展示的已保存上游：主键（库生成的整数，关联一律走它）、对外编号 <see cref="Uid"/>（32 位小写十六进制，保存前生成；官方账户的账户目录按它命名，只交给官方分支）、
/// 供应商名称（只做显示）、保存时基于的活动版本、配置、检测状态、最近一次报告、此刻能不能删除（按 <see cref="UpstreamCandidate.Deletable"/> 与删除时的同一判据给出，页面只读它）、导出搬走的时刻（没导出过为空）、
/// 此刻的检测阶段（按 <see cref="UpstreamCandidate.Phase"/> 推出，页面判「正在检测」「检测未完成」只读它）与配置是否损坏（损坏时 <see cref="Options"/> 是占位，页面只读出损坏与下一步）；不含任何秘密。
/// 库里那一行是实体 <see cref="UpstreamCandidate"/>，这里是解析好配置与报告、给页面读的视图。
/// </summary>
public sealed record UpstreamCandidateView(long Id, string Uid, string Name, long ExpectedVersion, UpstreamOptions Options, CandidateState State, UpstreamReport? Report, bool Deletable,
    DateTimeOffset? ExportedAt = null, UpstreamCheckPhase Phase = UpstreamCheckPhase.Idle, bool Damaged = false);
