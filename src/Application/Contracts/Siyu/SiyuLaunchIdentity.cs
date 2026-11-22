namespace Application.Contracts.Siyu;

/// <summary>
/// 写进思域配置目录的身份：<see cref="DeviceId"/> 是站点设备编号（写 .claude.json 的 userID）；<see cref="OAuthAccountJson"/> 是官方账户 oauthAccount 原字节；
/// <see cref="CredentialMetadataJson"/> 是 scopes、subscriptionType、rateLimitTier 三项（与个人令牌一起写进 .credentials.json，expiresAt 为 null、不写 refreshToken）。不含任何官方令牌。
/// </summary>
public sealed record SiyuLaunchIdentity(string DeviceId, ReadOnlyMemory<byte> OAuthAccountJson, ReadOnlyMemory<byte> CredentialMetadataJson);
