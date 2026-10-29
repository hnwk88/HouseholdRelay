using Core.Identity;

namespace Application.Contracts.Identity;

/// <summary>令牌轮换提交后的结果：使用者显示名与已校验的最新鉴权名单。</summary>
public sealed record RotatedIdentity(string DisplayName, IReadOnlyList<ClientAccess> Access);
