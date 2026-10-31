namespace Application.Contracts.Upstream;

/// <summary>
/// 库中上游的原始行：版本、选项与检测报告 JSON、加密凭据，以及它来自哪个已保存上游（主键与当前名称，未关联时为空）、自动切换开关，以及来源上游是否已导出搬走；由服务解析和解密。
/// 读活动上游时就是活动行；读备用上游时版本是读取时的活动版本，开关恒为 false。
/// </summary>
public sealed record StoredUpstream(long Version, string OptionsJson, byte[]? ProtectedCredential, string? ReportJson, string? CandidateId = null, string? Name = null, bool Failover = false, bool Exported = false);
