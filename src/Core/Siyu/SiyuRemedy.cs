namespace Core.Siyu;

/// <summary>
/// 一次打回：只说下一步做什么，对的值仍只从建会话回应一处给。<see cref="ExpectedIdentityVersion"/> 只在身份代次过时时有，下一次建会话带它；
/// <see cref="RetryAfterSeconds"/> 只在 <see cref="SiyuRemedyAction.Wait"/> 时有。
/// </summary>
public sealed record SiyuRemedy(SiyuRemedyAction Action, long? ExpectedIdentityVersion = null, int? RetryAfterSeconds = null);
