using Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

/// <summary>使用者映射到 users；内部名称唯一。</summary>
internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    /// <summary>映射列与唯一约束。</summary>
    /// <param name="builder">实体配置。</param>
    public void Configure(EntityTypeBuilder<User> builder)
    {
        EntityColumns.Map(builder, "users");
        builder.Property(item => item.Name).HasColumnName("name").IsRequired();
        builder.Property(item => item.DisplayName).HasColumnName("display_name").IsRequired();
        builder.Property(item => item.Enabled).HasColumnName("enabled");
        builder.HasIndex(item => item.Name).IsUnique();
    }
}
