using Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

/// <summary>管理会话映射到 admin_sessions；凭据哈希唯一，管理员走外键。</summary>
internal sealed class AdministratorSessionConfiguration : IEntityTypeConfiguration<AdministratorSession>
{
    /// <summary>映射列、唯一约束与外键。</summary>
    /// <param name="builder">实体配置。</param>
    public void Configure(EntityTypeBuilder<AdministratorSession> builder)
    {
        EntityColumns.Map(builder, "admin_sessions");
        builder.Property(item => item.TokenHash).HasColumnName("token_hash").IsRequired();
        builder.Property(item => item.AdministratorId).HasColumnName("admin_id");
        builder.Property(item => item.ExpiresAt).HasColumnName("expires_at");
        builder.HasIndex(item => item.TokenHash).IsUnique();
        builder.HasOne<Administrator>().WithMany().HasForeignKey(item => item.AdministratorId).OnDelete(DeleteBehavior.NoAction);
    }
}
