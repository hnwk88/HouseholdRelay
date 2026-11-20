using System.Linq.Expressions;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Repositories;

namespace Infrastructure.Persistence;

/// <summary>
/// 本实例数据库的 EF Core 上下文，按作用域创建、一次操作一个工作单元。模型只描述库版本 11（建表与升级在迁移链里，不用 EF Migrations）。
/// 提交前统一做四件事：新增行记创建与更新时刻，改动行记更新时刻，每个新增或改动的行换一个新的行版本（SQLite 没有自动行版本，按读出时的旧值核对并发），
/// 没有登记为物理删除的删除一律改成软删除；登记过的（<see cref="IHardDeleteTracker"/>）才真从库里删掉。
/// </summary>
internal sealed class RelayDbContext(DbContextOptions<RelayDbContext> options, IDateTimeProvider clock) : DbContext(options), IHardDeleteTracker
{
    /// <summary>本工作单元里已由应用层确认物理删除的实体（按引用比较）。</summary>
    private readonly HashSet<object> _hardDeletes = new(ReferenceEqualityComparer.Instance);

    /// <summary>全模型统一：DateTimeOffset 存 Unix 毫秒整数，decimal 存十进制文本。</summary>
    /// <param name="configurationBuilder">约定配置。</param>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UnixMillisecondsConverter>();
        configurationBuilder.Properties<decimal>().HaveConversion<DecimalTextConverter>();
    }

    /// <summary>读入本程序集里的全部实体配置，并给每个实体挂上命名的软删除过滤器（包含软删除查询时只关这一个）。</summary>
    /// <param name="modelBuilder">模型配置。</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RelayDbContext).Assembly);
        foreach (var type in modelBuilder.Model.GetEntityTypes().Where(type => typeof(BaseEntity).IsAssignableFrom(type.ClrType)).ToArray())
        {
            var entity = Expression.Parameter(type.ClrType, "entity");
            var filter = Expression.Lambda(Expression.Not(Expression.Property(entity, nameof(BaseEntity.IsDeleted))), entity);
            modelBuilder.Entity(type.ClrType).HasQueryFilter(RepositoryFilterNames.SoftDeletion, filter);
        }
    }

    /// <summary>登记一个已由应用层确认物理删除的实体。</summary>
    /// <param name="entity">要物理删除的实体。</param>
    public void MarkHardDelete(object entity) => _hardDeletes.Add(entity);

    /// <summary>这个实体是否已登记为物理删除。</summary>
    /// <param name="entity">实体。</param>
    public bool IsHardDelete(object entity) => _hardDeletes.Contains(entity);

    /// <summary>提交成功后清掉本工作单元的物理删除登记。</summary>
    public void ClearHardDeleteMarks() => _hardDeletes.Clear();

    /// <summary>补审计字段与行版本、把未登记的删除改成软删除后提交；成功后清掉物理删除登记。</summary>
    /// <param name="acceptAllChangesOnSuccess">提交成功后是否接受全部改动。</param>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        var count = base.SaveChanges(acceptAllChangesOnSuccess);
        ClearHardDeleteMarks();
        return count;
    }

    /// <summary>补审计字段与行版本、把未登记的删除改成软删除后提交；成功后清掉物理删除登记。</summary>
    /// <param name="acceptAllChangesOnSuccess">提交成功后是否接受全部改动。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        var count = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        ClearHardDeleteMarks();
        return count;
    }

    /// <summary>按每个跟踪中实体的状态补审计字段、换行版本，并把未登记为物理删除的删除改成软删除。</summary>
    private void Stamp()
    {
        ChangeTracker.DetectChanges();
        var now = clock.UtcNowOffset;
        foreach (EntityEntry<BaseEntity> entry in ChangeTracker.Entries<BaseEntity>().ToArray())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now; entry.Entity.UpdatedAt = now; entry.Entity.RowVersion = NewRowVersion();
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now; entry.Entity.RowVersion = NewRowVersion();
                    break;
                case EntityState.Deleted when !IsHardDelete(entry.Entity):
                    // 普通删除请求一律软删除：行留在库里，外键引用永远有效；真要从库里删掉必须经仓储登记物理删除。
                    entry.State = EntityState.Modified;
                    entry.Entity.IsDeleted = true; entry.Entity.DeletedAt ??= now; entry.Entity.UpdatedAt = now; entry.Entity.RowVersion = NewRowVersion();
                    break;
            }
        }
    }

    /// <summary>新的 8 字节随机行版本。</summary>
    private static byte[] NewRowVersion() => RandomNumberGenerator.GetBytes(8);
}
