using Application.Interfaces.Relay;

namespace Application.Services.Relay;

/// <summary>从请求里取个人中转令牌；中转入口和个人用量入口共用这一份。</summary>
internal static class RelayToken
{
    /// <summary>从 Authorization: Bearer 或 x-api-key 头取个人中转令牌；两个头同时出现且值不同时返回空，按无令牌处理，避免含糊的身份。</summary>
    internal static string? From(IRelayExchange exchange)
    {
        var authorization = exchange.Header("Authorization").ToString();
        var bearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..] : null;
        var apiKey = exchange.Header("x-api-key").ToString();
        if (bearer is not null && apiKey.Length > 0 && bearer != apiKey) return null;
        return bearer ?? (apiKey.Length > 0 ? apiKey : null);
    }
}
