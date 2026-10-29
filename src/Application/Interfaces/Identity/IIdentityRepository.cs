using Application.Contracts.Identity;
using Core.Identity;

namespace Application.Interfaces.Identity;

/// <summary>使用者与令牌的持久化；每次写入在同一事务里校验并返回提交后的鉴权名单，校验失败整体回滚。</summary>
public interface IIdentityRepository
{
    /// <summary>按创建顺序列出使用者及其当前令牌版本。</summary>
    IReadOnlyList<RelayUser> List();

    /// <summary>读取已提交的鉴权名单（只含当前有效令牌的哈希）。</summary>
    IReadOnlyList<ClientAccess> ReadAccess();

    /// <summary>新建使用者与第一版令牌；超出 8 人或名称重复时拒绝。</summary>
    IReadOnlyList<ClientAccess> Create(string userId, string name, string displayName, string tokenHash);

    /// <summary>修改显示名；使用者不存在时拒绝。</summary>
    IReadOnlyList<ClientAccess> Rename(string userId, string displayName);

    /// <summary>启用或停用使用者；使用者不存在时拒绝。</summary>
    IReadOnlyList<ClientAccess> SetEnabled(string userId, bool enabled);

    /// <summary>撤销当前令牌并写入下一版令牌哈希，保留启用状态。</summary>
    RotatedIdentity Rotate(string userId, string tokenHash);
}
