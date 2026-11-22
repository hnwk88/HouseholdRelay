namespace Core.Siyu;

/// <summary>
/// 核对过字段表的一份官方账户启动资料，只含可下发给思域客户端的非秘密项：
/// <see cref="OAuthAccountJson"/> 是中转机官方账户目录里 oauthAccount 那一段的原始字节；
/// <see cref="CredentialMetadataJson"/> 是只含 scopes、subscriptionType、rateLimitTier 的 JSON 对象（缺的项不写）；
/// <see cref="ProfileVersion"/> 是资料代次，同样内容算出同样代次。官方令牌永不在这里。
/// </summary>
public sealed class SiyuLaunchProfile
{
    private readonly byte[] _oauthAccount;
    private readonly byte[] _credentialMetadata;

    /// <summary>由 <see cref="SiyuLaunchProfileContract.Read"/> 构造。</summary>
    internal SiyuLaunchProfile(string accountUuid, byte[] oauthAccount, byte[] credentialMetadata, string profileVersion)
    {
        AccountUuid = accountUuid;
        _oauthAccount = oauthAccount;
        _credentialMetadata = credentialMetadata;
        ProfileVersion = profileVersion;
    }

    /// <summary>oauthAccount.accountUuid。</summary>
    public string AccountUuid { get; }

    /// <summary>oauthAccount 原始字节，原样写进思域目录的 .claude.json。</summary>
    public ReadOnlyMemory<byte> OAuthAccountJson => _oauthAccount;

    /// <summary>三项凭据资料的 JSON 对象，写进思域目录 .credentials.json 的 claudeAiOauth。</summary>
    public ReadOnlyMemory<byte> CredentialMetadataJson => _credentialMetadata;

    /// <summary>资料代次：16 个小写十六进制字符。</summary>
    public string ProfileVersion { get; }
}
