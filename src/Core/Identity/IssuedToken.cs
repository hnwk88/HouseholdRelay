namespace Core.Identity;

/// <summary>新签发或轮换后的个人令牌；明文只在本次返回中出现，不进入持久化模型。</summary>
public sealed record IssuedToken(string UserId, string DisplayName, string Token);
