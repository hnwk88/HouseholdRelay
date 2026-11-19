using Core.Identity;

namespace Application.Contracts.Identity;

/// <summary>新建使用者提交后的结果：库生成的使用者主键与已校验的最新鉴权名单。</summary>
public sealed record CreatedIdentity(long UserId, IReadOnlyList<ClientAccess> Access);
