namespace Application.Contracts.Upstream;

/// <summary>库中已保存上游的非秘密部分：主键（库生成的整数）、对外编号（官方账户目录名）、供应商名称、保存时的活动版本、选项 JSON、状态、检测报告 JSON，此刻能不能删除（不是当前使用中、也不在检测窗口内，与删除事务同一判据），以及导出搬走的时刻（Unix 毫秒，没导出过为空）。</summary>
public sealed record StoredCandidate(long Id, string Uid, string Name, long ExpectedVersion, string OptionsJson, string State, string? ReportJson, bool Deletable, long? ExportedAt = null);
