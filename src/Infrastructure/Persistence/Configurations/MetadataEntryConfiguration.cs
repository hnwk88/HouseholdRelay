using Core.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

/// <summary>实例元数据映射到 metadata；键名唯一。</summary>
internal sealed class MetadataEntryConfiguration : IEntityTypeConfiguration<MetadataEntry>
{
    /// <summary>映射列与唯一约束。</summary>
    /// <param name="builder">实体配置。</param>
    public void Configure(EntityTypeBuilder<MetadataEntry> builder)
    {
        EntityColumns.Map(builder, "metadata");
        builder.Property(item => item.Key).HasColumnName("key").IsRequired();
        builder.Property(item => item.Value).HasColumnName("value").IsRequired();
        builder.HasIndex(item => item.Key).IsUnique();
    }
}
