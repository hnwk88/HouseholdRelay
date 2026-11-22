namespace Core.Siyu;

/// <summary>
/// 一次取到的有效官方凭据：只在中转内部用，<see cref="AccessToken"/> 只写进发往官方的那一个请求头。
/// <see cref="Generation"/> 是本服务进程里这个账户实际访问令牌的单调代次（令牌内容变了才加一），迟到的 401/403 按它归属，不误伤新代次。
/// 普通类、不提供序列化，<see cref="ToString"/> 不含令牌。
/// </summary>
public sealed class OfficialCredentialLease(string accountId, string accessToken, DateTimeOffset expiresAt, long generation)
{
    /// <summary>官方账户目录键（上游的 Uid）。</summary>
    public string AccountId { get; } = accountId;

    /// <summary>官方访问令牌。</summary>
    public string AccessToken { get; } = accessToken;

    /// <summary>凭据文件里记的真实到期时刻。</summary>
    public DateTimeOffset ExpiresAt { get; } = expiresAt;

    /// <summary>凭据代次，从 1 开始。</summary>
    public long Generation { get; } = generation;

    /// <summary>只给账户与代次，绝不带令牌。</summary>
    public override string ToString() => $"official credential {AccountId} generation {Generation}";
}
