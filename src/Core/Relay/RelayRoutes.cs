using Core.Usage;

namespace Core.Relay;

/// <summary>
/// 中转对外开放的模型接口只在这里登记一次：哪三个路由、各用什么方法、各记成哪一类用量。转发入口按它放行，发往上游的地址校验、协议诊断的路由脱敏都取它。
/// </summary>
public static class RelayRoutes
{
    /// <summary>对话（推理）。</summary>
    public const string Messages = "/v1/messages";

    /// <summary>估算 Token。</summary>
    public const string CountTokens = "/v1/messages/count_tokens";

    /// <summary>模型目录。</summary>
    public const string Models = "/v1/models";

    /// <summary>开放的路由与各自允许的方法。</summary>
    public static IReadOnlyDictionary<string, string> Exposed { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    { [Messages] = "POST", [CountTokens] = "POST", [Models] = "GET" };

    /// <summary>是不是开放的路由（区分大小写）。</summary>
    public static bool IsExposed(string route) => Exposed.ContainsKey(route);

    /// <summary>开放路由记成哪一类用量：对话是推理，模型目录是模型查询，估算是估算。</summary>
    public static UsageKind Kind(string route) => route == Messages ? UsageKind.Inference : route == Models ? UsageKind.Models : UsageKind.Estimate;
}
