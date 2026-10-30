namespace Core.Usage;

/// <summary>某个已保存上游（按主键）下某个模型的每百万计数单价；未知单价为空。</summary>
public sealed record UsagePrice(string Id, string CandidateId, string Model, string Currency,
    decimal? InputPerMillion, decimal? OutputPerMillion, decimal? CacheReadPerMillion, decimal? CacheWritePerMillion);
