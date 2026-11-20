using Core.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

/// <summary>实例设置映射到单行表 settings。</summary>
internal sealed class RelaySettingsConfiguration : IEntityTypeConfiguration<RelaySettings>
{
    /// <summary>映射列。</summary>
    /// <param name="builder">实体配置。</param>
    public void Configure(EntityTypeBuilder<RelaySettings> builder)
    {
        EntityColumns.Map(builder, "settings", singleton: true);
        builder.Property(item => item.Version).HasColumnName("version");
        builder.Property(item => item.Json).HasColumnName("json").IsRequired();
    }
}
