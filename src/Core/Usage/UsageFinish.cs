namespace Core.Usage;

/// <summary>保存一次请求结束结果的结论：这次写了、早已结束无需再写（重复的结束通知不覆盖首次结果）、存储故障重试后仍没写进去。只是返回值，不进库。</summary>
public enum UsageFinish
{
    /// <summary>这次写进去了。</summary>
    Saved,

    /// <summary>这条记录早已结束，什么都没写。</summary>
    AlreadyEnded,

    /// <summary>存储故障，重试后仍没写进去（已计入失败次数）。</summary>
    Failed,
}
