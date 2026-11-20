namespace Repositories;

/// <summary>
/// 使用系统时钟提供本地时间和 UTC 时间。
/// </summary>
public sealed class DateTimeProvider : IDateTimeProvider
{
    /// <summary>
    /// 获取当前本地时间。
    /// </summary>
    public DateTime Now => DateTime.Now;

    /// <summary>
    /// 获取当前 UTC 时间。
    /// </summary>
    public DateTime UtcNow => DateTime.UtcNow;

    /// <summary>
    /// 获取带时区偏移的当前 UTC 时间。
    /// </summary>
    public DateTimeOffset UtcNowOffset => DateTimeOffset.UtcNow;
}
