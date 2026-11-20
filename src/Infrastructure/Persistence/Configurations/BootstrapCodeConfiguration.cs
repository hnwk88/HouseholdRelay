using Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

/// <summary>一次性首次设置码映射到单行表 bootstrap。</summary>
internal sealed class BootstrapCodeConfiguration : IEntityTypeConfiguration<BootstrapCode>
{
    /// <summary>映射列。</summary>
    /// <param name="builder">实体配置。</param>
    public void Configure(EntityTypeBuilder<BootstrapCode> builder)
    {
        EntityColumns.Map(builder, "bootstrap", singleton: true);
        builder.Property(item => item.Hash).HasColumnName("hash").IsRequired();
        builder.Property(item => item.Protected).HasColumnName("protected").IsRequired();
    }
}
