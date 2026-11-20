using Core.Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

/// <summary>密文映射到 secrets；名字唯一。</summary>
internal sealed class ProtectedSecretConfiguration : IEntityTypeConfiguration<ProtectedSecret>
{
    /// <summary>映射列与唯一约束。</summary>
    /// <param name="builder">实体配置。</param>
    public void Configure(EntityTypeBuilder<ProtectedSecret> builder)
    {
        EntityColumns.Map(builder, "secrets");
        builder.Property(item => item.Name).HasColumnName("name").IsRequired();
        builder.Property(item => item.Protected).HasColumnName("protected").IsRequired();
        builder.HasIndex(item => item.Name).IsUnique();
    }
}
