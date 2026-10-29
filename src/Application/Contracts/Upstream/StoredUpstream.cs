namespace Application.Contracts.Upstream;

/// <summary>库中活动上游的原始行：版本、选项与检测报告 JSON、加密凭据；由服务解析和解密。</summary>
public sealed record StoredUpstream(long Version, string OptionsJson, byte[]? ProtectedCredential, string? ReportJson);
