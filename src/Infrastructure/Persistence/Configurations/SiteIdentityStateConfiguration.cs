using Core.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

/// <summary>站点身份当前状态映射到单行表 site_identity；最近一次维护操作走外键。</summary>
internal sealed class SiteIdentityStateConfiguration : IEntityTypeConfiguration<SiteIdentityState>
{
    /// <summary>映射列与外键。</summary>
    /// <param name="builder">实体配置。</param>
    public void Configure(EntityTypeBuilder<SiteIdentityState> builder)
    {
        EntityColumns.Map(builder, "site_identity", singleton: true);
        builder.Property(item => item.Version).HasColumnName("version");
        builder.Property(item => item.SiteIdentityOperationId).HasColumnName("site_identity_operation_id");
        builder.Property(item => item.Pending).HasColumnName("pending");
        builder.HasOne<SiteIdentityOperation>().WithMany().HasForeignKey(item => item.SiteIdentityOperationId).OnDelete(DeleteBehavior.NoAction);
    }
}
