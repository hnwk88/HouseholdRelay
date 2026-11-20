namespace Repositories;

/// <summary>
/// 表示支持统一主键、审计字段、软删除和乐观并发控制的通用持久化实体基类。
/// </summary>
/// <remarks>
/// 该类型不包含任何具体工程名称、业务表名称或数据库技术，实现仓储模块迁移到其它工程时无需改名。
/// 当前主键固定使用 <see cref="long" />，由 SQL Server 顺序生成；受控旧库迁移可以显式写入原长整数主键。
/// </remarks>
public abstract class BaseEntity
{
    /// <summary>
    /// 获取或设置实体主键。
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// 获取或设置创建时间；通用约定使用 UTC 时间保存。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 获取或设置创建人或创建来源；为空表示当前调用上下文没有提供操作者。
    /// </summary>
    public string? CreatedBy { get; set; }

    /// <summary>
    /// 获取或设置最后更新时间；通用约定使用 UTC 时间保存。
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 获取或设置最后更新人或更新来源；为空表示当前调用上下文没有提供操作者。
    /// </summary>
    public string? UpdatedBy { get; set; }

    /// <summary>
    /// 获取或设置是否已经软删除；普通查询默认排除软删除数据。
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// 获取或设置软删除时间；实体未软删除时为空。
    /// </summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// 获取或设置删除人或删除来源；为空表示当前调用上下文没有提供操作者。
    /// </summary>
    public string? DeletedBy { get; set; }

    /// <summary>
    /// 获取或设置数据库行版本，用于乐观并发控制。
    /// </summary>
    public byte[]? RowVersion { get; set; }
}
