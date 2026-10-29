using Core.Identity;

namespace Application.Interfaces.Identity;

/// <summary>网页和命令行共用的使用者管理；明文令牌只在签发结果里出现一次，不进入持久化模型。</summary>
public interface IIdentityService
{
    /// <summary>中转鉴权使用的令牌名单；每次提交后立即发布新快照。</summary>
    ClientRegistry Clients { get; }

    /// <summary>列出使用者。</summary>
    IReadOnlyList<RelayUser> List();

    /// <summary>读取已提交的鉴权名单。</summary>
    IReadOnlyList<ClientAccess> ReadAccess();

    /// <summary>新建使用者并签发第一版个人令牌；旧客户端迁移时可指定原名称。</summary>
    IssuedToken Create(string displayName, string? legacyName = null);

    /// <summary>修改显示名，令牌不变。</summary>
    void Rename(string userId, string displayName);

    /// <summary>启用或停用使用者，鉴权立即生效。</summary>
    void SetEnabled(string userId, bool enabled);

    /// <summary>轮换个人令牌，旧令牌立即失效。</summary>
    IssuedToken Rotate(string userId);
}
