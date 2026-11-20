using Repositories;

namespace Core.Identity;

/// <summary>一个管理会话（表 admin_sessions）：会话凭据的哈希（唯一，按它查找）、属于哪位管理员、到期时刻。过期与退出都物理删除。</summary>
public sealed class AdministratorSession : BaseEntity
{
    /// <summary>会话凭据的哈希；Cookie 里是明文凭据，库里只有它的哈希。</summary>
    public string TokenHash { get; set; } = "";

    /// <summary>会话属于的管理员主键。</summary>
    public long AdministratorId { get; set; }

    /// <summary>到期时刻（Unix 秒）。</summary>
    public long ExpiresAt { get; set; }

    /// <summary>在 <paramref name="nowSeconds"/> 这一刻是否仍有效（严格早于到期时刻）。</summary>
    /// <param name="nowSeconds">此刻（Unix 秒）。</param>
    public bool ValidAt(long nowSeconds) => ExpiresAt > nowSeconds;
}
