using Core.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

/// <summary>站点维护操作记录映射到 site_identity_operations；操作编号与代次各自唯一。</summary>
internal sealed class SiteIdentityOperationConfiguration : IEntityTypeConfiguration<SiteIdentityOperation>
{
    /// <summary>映射列与唯一约束。</summary>
    /// <param name="builder">实体配置。</param>
    public void Configure(EntityTypeBuilder<SiteIdentityOperation> builder)
    {
        EntityColumns.Map(builder, "site_identity_operations");
        builder.Property(item => item.OperationId).HasColumnName("operation_id").IsRequired();
        builder.Property(item => item.Version).HasColumnName("version");
        builder.Property(item => item.Pending).HasColumnName("pending");
        builder.HasIndex(item => item.OperationId).IsUnique();
        builder.HasIndex(item => item.Version).IsUnique();
    }
}
