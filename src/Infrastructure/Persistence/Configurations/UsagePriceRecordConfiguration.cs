using Core.Upstream;
using Core.Usage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

/// <summary>单价映射到 usage_prices；同一家同一模型唯一，已保存上游走外键。</summary>
internal sealed class UsagePriceRecordConfiguration : IEntityTypeConfiguration<UsagePriceRecord>
{
    /// <summary>映射列、唯一约束与外键。</summary>
    /// <param name="builder">实体配置。</param>
    public void Configure(EntityTypeBuilder<UsagePriceRecord> builder)
    {
        EntityColumns.Map(builder, "usage_prices");
        builder.Property(item => item.UpstreamCandidateId).HasColumnName("candidate_id");
        builder.Property(item => item.Model).HasColumnName("model").IsRequired();
        builder.Property(item => item.Json).HasColumnName("json").IsRequired();
        builder.HasIndex(item => new { item.UpstreamCandidateId, item.Model }).IsUnique();
        builder.HasOne<UpstreamCandidate>().WithMany().HasForeignKey(item => item.UpstreamCandidateId).OnDelete(DeleteBehavior.NoAction);
    }
}
