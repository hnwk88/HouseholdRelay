using Core.Upstream;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

/// <summary>已保存上游映射到 upstream_candidates；对外编号唯一。</summary>
internal sealed class UpstreamCandidateConfiguration : IEntityTypeConfiguration<UpstreamCandidate>
{
    /// <summary>映射列与唯一约束。</summary>
    /// <param name="builder">实体配置。</param>
    public void Configure(EntityTypeBuilder<UpstreamCandidate> builder)
    {
        EntityColumns.Map(builder, "upstream_candidates");
        builder.Property(item => item.Uid).HasColumnName("uid").IsRequired();
        builder.Property(item => item.ExpectedVersion).HasColumnName("expected_version");
        builder.Property(item => item.OptionsJson).HasColumnName("options_json").IsRequired();
        builder.Property(item => item.Credential).HasColumnName("credential").IsRequired();
        builder.Property(item => item.State).HasColumnName("state").IsRequired();
        builder.Property(item => item.ReportJson).HasColumnName("report_json");
        builder.Property(item => item.TestStartedAt).HasColumnName("test_started_at");
        builder.Property(item => item.Name).HasColumnName("name").IsRequired();
        builder.Property(item => item.ExportedAt).HasColumnName("exported_at");
        builder.HasIndex(item => item.Uid).IsUnique();
    }
}
