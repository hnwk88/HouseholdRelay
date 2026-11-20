using Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

/// <summary>令牌映射到 tokens；哈希唯一、同一使用者的版本号唯一、每名使用者最多一版未撤销，使用者走外键。</summary>
internal sealed class TokenConfiguration : IEntityTypeConfiguration<Token>
{
    /// <summary>映射列、唯一约束与外键。</summary>
    /// <param name="builder">实体配置。</param>
    public void Configure(EntityTypeBuilder<Token> builder)
    {
        EntityColumns.Map(builder, "tokens");
        builder.Property(item => item.UserId).HasColumnName("user_id");
        builder.Property(item => item.Version).HasColumnName("version");
        builder.Property(item => item.Hash).HasColumnName("hash").IsRequired();
        builder.Property(item => item.RevokedAt).HasColumnName("revoked_at");
        builder.HasIndex(item => item.Hash).IsUnique();
        builder.HasIndex(item => new { item.UserId, item.Version }).IsUnique();
        builder.HasIndex(item => item.UserId).IsUnique().HasFilter("revoked_at IS NULL").HasDatabaseName("one_active_token");
        builder.HasOne<User>().WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.NoAction);
    }
}
