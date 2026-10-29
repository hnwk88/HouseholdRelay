namespace Core.Usage;

/// <summary>某个活动上游版本下某个模型的每百万计数单价；未知单价为空。</summary>
public sealed record UsagePrice(string Id, long UpstreamVersion, string Model, string Currency,
    decimal? InputPerMillion, decimal? OutputPerMillion, decimal? CacheReadPerMillion, decimal? CacheWritePerMillion);
