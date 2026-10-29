namespace Application.Contracts.Upstream;

/// <summary>库中已保存上游的非秘密部分：主键、供应商名称、保存时的活动版本、选项 JSON、状态与检测报告 JSON。</summary>
public sealed record StoredCandidate(string Id, string Name, long ExpectedVersion, string OptionsJson, string State, string? ReportJson);
