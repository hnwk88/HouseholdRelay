namespace Core.Usage;

/// <summary>用量记录保留多久只在这里定义一次：清理、查询窗口与页面上的说明都取它。</summary>
public static class UsageRetention
{
    /// <summary>保留最近这么多天（按请求开始时间的 UTC 滚动窗口）。</summary>
    public const int Days = 40;

    /// <summary>此刻的保留截止点：早于它的记录会被清理，也不进查询。</summary>
    public static DateTimeOffset Cutoff(DateTimeOffset now) => now.AddDays(-Days);
}
