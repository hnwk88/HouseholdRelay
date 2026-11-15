namespace Core.Upstream;

/// <summary>
/// 官方 <c>/api/oauth/profile</c> 返回的账户资料里，原生 <c>claude auth login</c> 写进 .claude.json 顶层 oauthAccount 的三项：
/// <paramref name="AccountUuid"/>（account.uuid，标准 UUID）、<paramref name="EmailAddress"/>（account.email）、<paramref name="OrganizationUuid"/>（organization.uuid）。
/// 只用来把发往官方的 metadata.user_id 账户段对齐原生直连，不落库、不进日志。
/// </summary>
public sealed record OfficialAccountProfile(string AccountUuid, string? EmailAddress, string? OrganizationUuid);
