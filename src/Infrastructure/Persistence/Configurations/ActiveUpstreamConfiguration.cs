using Core.Upstream;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

/// <summary>活动上游映射到单行表 upstream；来源已保存上游走外键。</summary>
internal sealed class ActiveUpstreamConfiguration : IEntityTypeConfiguration<ActiveUpstream>
{
    /// <summary>映射列与外键。</summary>
    /// <param name="builder">实体配置。</param>
    public void Configure(EntityTypeBuilder<ActiveUpstream> builder)
    {
        EntityColumns.Map(builder, "upstream", singleton: true);
        builder.Property(item => item.Version).HasColumnName("version");
        builder.Property(item => item.Mode).HasColumnName("mode").IsRequired();
        builder.Property(item => item.BaseUrl).HasColumnName("base_url").IsRequired();
        builder.Property(item => item.Credential).HasColumnName("credential");
        builder.Property(item => item.OptionsJson).HasColumnName("options_json");
        builder.Property(item => item.ReportJson).HasColumnName("report_json");
        builder.Property(item => item.UpstreamCandidateId).HasColumnName("candidate_id");
        builder.Property(item => item.Failover).HasColumnName("failover");
        builder.HasOne<UpstreamCandidate>().WithMany().HasForeignKey(item => item.UpstreamCandidateId).OnDelete(DeleteBehavior.NoAction);
    }
}
