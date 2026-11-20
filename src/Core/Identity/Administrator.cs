using Repositories;

namespace Core.Identity;

/// <summary>唯一的管理员（表 administrators，库里用唯一索引限定最多一行）：只存密码哈希。</summary>
public sealed class Administrator : BaseEntity
{
    /// <summary>管理密码的哈希。</summary>
    public string PasswordHash { get; set; } = "";
}
