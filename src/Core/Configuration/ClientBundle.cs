namespace Core.Configuration;

/// <summary>旧客户端包里的 client.json：中转根地址与个人令牌。</summary>
public sealed record ClientBundle(string BaseUrl, string Token);
