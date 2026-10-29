namespace Application.Contracts.Upstream;

/// <summary>库中候选上游的非秘密部分：期望的活动版本、选项 JSON、状态与检测报告 JSON。</summary>
public sealed record StoredCandidate(long ExpectedVersion, string OptionsJson, string State, string? ReportJson);
