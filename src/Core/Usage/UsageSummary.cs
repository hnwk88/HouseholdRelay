namespace Core.Usage;

/// <summary>同一接口类别的请求数、结果分布、计数合计与分币种金额合计。</summary>
public sealed record UsageSummary(string Kind, long Requests, long Succeeded, long Failed, long Cancelled,
    long Interrupted, long Pending, long NotSent, long Incomplete, UsageTokens Tokens)
{
    /// <summary>该类别中金额来源未知的推理请求数，用来提示金额合计不包含这些请求。</summary>
    public long UnknownAmounts { get; init; }

    /// <summary>按币种和金额来源分别合计的金额；不同币种、不同来源各占一项，从不相加。</summary>
    public IReadOnlyList<MoneyTotal> Money { get; init; } = [];
}
