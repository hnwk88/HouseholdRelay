using Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

/// <summary>管理员映射到 administrators；最多一行由库里的表达式唯一索引 one_administrator 兜底（EF 模型表达不了表达式索引，比对用例单列说明）。</summary>
internal sealed class AdministratorConfiguration : IEntityTypeConfiguration<Administrator>
{
    /// <summary>映射列。</summary>
    /// <param name="builder">实体配置。</param>
    public void Configure(EntityTypeBuilder<Administrator> builder)
    {
        EntityColumns.Map(builder, "administrators");
        builder.Property(item => item.PasswordHash).HasColumnName("password_hash").IsRequired();
    }
}
