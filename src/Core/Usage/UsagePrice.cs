namespace Core.Usage;

/// <summary>
/// 某个模型的每百万计数单价；未知单价为空。只装费率：属于哪个已保存上游看单价表的列，发送时整份复制进用量记录作快照。
/// 版本 10 之前存下的 JSON 还多带文本的 Id 与 CandidateId，反序列化时按未知字段忽略，不再承担任何关系。
/// </summary>
public sealed record UsagePrice(string Model, string Currency,
    decimal? InputPerMillion, decimal? OutputPerMillion, decimal? CacheReadPerMillion, decimal? CacheWritePerMillion);
