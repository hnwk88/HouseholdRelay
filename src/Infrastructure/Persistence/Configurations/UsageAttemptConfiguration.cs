using Core.Upstream;
using Core.Usage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

/// <summary>上游尝试映射到 usage_attempts；同一用量内次序唯一，用量走级联外键（随用量物理删除一起删），已保存上游走普通外键。</summary>
internal sealed class UsageAttemptConfiguration : IEntityTypeConfiguration<UsageAttempt>
{
    /// <summary>映射列、唯一约束、索引与外键。</summary>
    /// <param name="builder">实体配置。</param>
    public void Configure(EntityTypeBuilder<UsageAttempt> builder)
    {
        EntityColumns.Map(builder, "usage_attempts");
        builder.Property(item => item.UsageRequestId).HasColumnName("usage_request_id");
        builder.Property(item => item.Attempt).HasColumnName("attempt");
        builder.Property(item => item.UpstreamCandidateId).HasColumnName("candidate_id");
        builder.Property(item => item.UpstreamVersion).HasColumnName("upstream_version");
        builder.Property(item => item.StartedAt).HasColumnName("started_at");
        builder.Property(item => item.HeadersMs).HasColumnName("headers_ms");
        builder.Property(item => item.DurationMs).HasColumnName("duration_ms");
        builder.Property(item => item.UpstreamStatus).HasColumnName("upstream_status");
        builder.Property(item => item.UpstreamRequestId).HasColumnName("upstream_request_id");
        builder.Property(item => item.ErrorType).HasColumnName("error_type");
        builder.Property(item => item.ErrorMessage).HasColumnName("error_message");
        builder.Property(item => item.ConnectFailure).HasColumnName("connect_failure");
        builder.Property(item => item.Disposition).HasColumnName("disposition").IsRequired();
        builder.Property(item => item.UpstreamName).HasColumnName("upstream_name");
        builder.HasIndex(item => new { item.UsageRequestId, item.Attempt }).IsUnique();
        builder.HasIndex(item => new { item.UpstreamCandidateId, item.StartedAt }).HasDatabaseName("usage_attempts_candidate");
        builder.HasOne<UsageRequest>().WithMany().HasForeignKey(item => item.UsageRequestId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<UpstreamCandidate>().WithMany().HasForeignKey(item => item.UpstreamCandidateId).OnDelete(DeleteBehavior.NoAction);
    }
}
