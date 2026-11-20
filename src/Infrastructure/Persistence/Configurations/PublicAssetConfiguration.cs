using Core.Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

/// <summary>公共资产映射到 public_assets；名字唯一。</summary>
internal sealed class PublicAssetConfiguration : IEntityTypeConfiguration<PublicAsset>
{
    /// <summary>映射列与唯一约束。</summary>
    /// <param name="builder">实体配置。</param>
    public void Configure(EntityTypeBuilder<PublicAsset> builder)
    {
        EntityColumns.Map(builder, "public_assets");
        builder.Property(item => item.Name).HasColumnName("name").IsRequired();
        builder.Property(item => item.Value).HasColumnName("value").IsRequired();
        builder.HasIndex(item => item.Name).IsUnique();
    }
}
