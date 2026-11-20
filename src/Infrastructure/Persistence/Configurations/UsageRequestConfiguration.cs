using Core.Identity;
using Core.Upstream;
using Core.Usage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

/// <summary>用量记录映射到 usage_requests；请求编号唯一，使用者、令牌、已保存上游走外键，查询用的五个索引与版本 10 同名同列。</summary>
internal sealed class UsageRequestConfiguration : IEntityTypeConfiguration<UsageRequest>
{
    /// <summary>映射列、唯一约束、索引与外键。</summary>
    /// <param name="builder">实体配置。</param>
    public void Configure(EntityTypeBuilder<UsageRequest> builder)
    {
        EntityColumns.Map(builder, "usage_requests");
        builder.Property(item => item.RequestId).HasColumnName("request_id").IsRequired();
        builder.Property(item => item.UserId).HasColumnName("user_id");
        builder.Property(item => item.TokenId).HasColumnName("token_id");
        builder.Property(item => item.UpstreamVersion).HasColumnName("upstream_version");
        builder.Property(item => item.RequestStartedAt).HasColumnName("request_started_at");
        builder.Property(item => item.EndedAt).HasColumnName("ended_at");
        builder.Property(item => item.Kind).HasColumnName("kind").IsRequired();
        builder.Property(item => item.Model).HasColumnName("model");
        builder.Property(item => item.Outcome).HasColumnName("outcome").IsRequired();
        builder.Property(item => item.Dispatched).HasColumnName("dispatched");
        builder.Property(item => item.StatusCode).HasColumnName("status_code");
        builder.Property(item => item.UpstreamStatus).HasColumnName("upstream_status");
        builder.Property(item => item.DurationMs).HasColumnName("duration_ms");
        builder.Property(item => item.Completeness).HasColumnName("completeness").IsRequired();
        builder.Property(item => item.InputTokens).HasColumnName("input_tokens");
        builder.Property(item => item.OutputTokens).HasColumnName("output_tokens");
        builder.Property(item => item.CacheReadTokens).HasColumnName("cache_read_tokens");
        builder.Property(item => item.CacheWriteTokens).HasColumnName("cache_write_tokens");
        builder.Property(item => item.PriceJson).HasColumnName("price_json");
        builder.Property(item => item.Amount).HasColumnName("amount");
        builder.Property(item => item.Currency).HasColumnName("currency");
        builder.Property(item => item.MoneySource).HasColumnName("money_source").IsRequired();
        builder.Property(item => item.MoneyCompleteness).HasColumnName("money_completeness").IsRequired();
        builder.Property(item => item.UpstreamCandidateId).HasColumnName("candidate_id");
        builder.Property(item => item.UpstreamName).HasColumnName("upstream_name");
        builder.HasIndex(item => item.RequestId).IsUnique();
        builder.HasIndex(item => new { item.RequestStartedAt, item.RequestId }).HasDatabaseName("usage_time");
        builder.HasIndex(item => new { item.UserId, item.RequestStartedAt, item.RequestId }).HasDatabaseName("usage_user_time");
        builder.HasIndex(item => new { item.Kind, item.RequestStartedAt }).HasDatabaseName("usage_kind_time");
        builder.HasIndex(item => new { item.Model, item.RequestStartedAt }).HasDatabaseName("usage_model_time");
        builder.HasIndex(item => new { item.Outcome, item.RequestStartedAt }).HasDatabaseName("usage_outcome_time");
        builder.HasOne<User>().WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Token>().WithMany().HasForeignKey(item => item.TokenId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<UpstreamCandidate>().WithMany().HasForeignKey(item => item.UpstreamCandidateId).OnDelete(DeleteBehavior.NoAction);
    }
}
