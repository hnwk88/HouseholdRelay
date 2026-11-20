namespace Repositories;

/// <summary>
/// 定义可替换的时间提供者，避免仓储测试直接依赖系统当前时间。
/// </summary>
public interface IDateTimeProvider
{
    /// <summary>
    /// 获取当前本地时间。
    /// </summary>
    DateTime Now { get; }

    /// <summary>
    /// 获取当前 UTC 时间。
    /// </summary>
    DateTime UtcNow { get; }

    /// <summary>
    /// 获取带时区偏移的当前 UTC 时间，供实体审计字段直接使用。
    /// </summary>
    DateTimeOffset UtcNowOffset { get; }
}
