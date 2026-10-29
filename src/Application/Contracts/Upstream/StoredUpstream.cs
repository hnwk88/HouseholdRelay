namespace Application.Contracts.Upstream;

/// <summary>库中活动上游的原始行：版本、选项与检测报告 JSON、加密凭据，以及它来自哪个已保存上游（主键与当前名称，未关联时为空）；由服务解析和解密。</summary>
public sealed record StoredUpstream(long Version, string OptionsJson, byte[]? ProtectedCredential, string? ReportJson, string? CandidateId = null, string? Name = null);
