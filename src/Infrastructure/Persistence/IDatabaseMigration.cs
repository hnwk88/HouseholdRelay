namespace Infrastructure.Persistence;

/// <summary>数据库打开后按 <see cref="Order"/> 依次执行的启动步骤（旧数据导入与架构升级）；每步自带事务并在已完成时直接返回。</summary>
public interface IDatabaseMigration
{
    /// <summary>执行顺序；数字小的先执行，顺序与迁移前保持一致。</summary>
    int Order { get; }

    /// <summary>对已通过完整性检查的数据库执行本步骤。</summary>
    void Apply(RelayDatabase database);
}
