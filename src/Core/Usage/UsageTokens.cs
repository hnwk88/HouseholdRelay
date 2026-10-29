namespace Core.Usage;

/// <summary>上游实际报告的可空计数；估算只使用输入列，缓存用量独立展示。</summary>
public sealed record UsageTokens(long? Input = null, long? Output = null, long? CacheRead = null, long? CacheWrite = null);
