using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Repositories;

namespace Infrastructure.Persistence;

/// <summary>每张表共有的 <see cref="BaseEntity"/> 八列与主键的映射；列名与 v11 建表语句一致（蛇形小写）。</summary>
internal static class EntityColumns
{
    /// <summary>
    /// 映射表名、主键 id 与审计、软删除、行版本列；行版本是并发令牌：改写时按读出时的值核对，对不上就抛出并发冲突。
    /// <paramref name="singleton"/> 为真时主键固定为 1，不由库生成（单行表）。
    /// </summary>
    /// <typeparam name="TEntity">实体类型。</typeparam>
    /// <param name="builder">实体配置。</param>
    /// <param name="table">表名。</param>
    /// <param name="singleton">是否单行表。</param>
    internal static void Map<TEntity>(EntityTypeBuilder<TEntity> builder, string table, bool singleton = false) where TEntity : BaseEntity
    {
        builder.ToTable(table);
        builder.HasKey(entity => entity.Id);
        var id = builder.Property(entity => entity.Id).HasColumnName("id");
        if (singleton) id.ValueGeneratedNever();
        builder.Property(entity => entity.CreatedAt).HasColumnName("created_at");
        builder.Property(entity => entity.CreatedBy).HasColumnName("created_by");
        builder.Property(entity => entity.UpdatedAt).HasColumnName("updated_at");
        builder.Property(entity => entity.UpdatedBy).HasColumnName("updated_by");
        builder.Property(entity => entity.IsDeleted).HasColumnName("is_deleted");
        builder.Property(entity => entity.DeletedAt).HasColumnName("deleted_at");
        builder.Property(entity => entity.DeletedBy).HasColumnName("deleted_by");
        builder.Property(entity => entity.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
    }
}
