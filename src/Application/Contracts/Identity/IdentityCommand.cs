namespace Application.Contracts.Identity;

/// <summary>一次使用者管理动作：只带动作、使用者主键与名称参数，不带任何令牌。新增时 <see cref="UserId"/> 为 null。</summary>
public sealed record IdentityCommand(IdentityCommandAction Action, long? UserId, string? DisplayName, string? Name);
