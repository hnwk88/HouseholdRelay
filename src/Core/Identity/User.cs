using Repositories;

namespace Core.Identity;

/// <summary>一名使用者（表 users）：内部名称（唯一，只做显示与旧客户端兼容）、显示名、是否启用。主键由库生成，令牌与用量都按它关联。</summary>
public sealed class User : BaseEntity
{
    /// <summary>一个实例最多几名使用者；轮换令牌不占新名额。</summary>
    public const int MaxUsers = 8;

    /// <summary>内部名称（<c>u_</c> 加随机十六进制，或旧客户端沿用的原名）。</summary>
    public string Name { get; set; } = "";

    /// <summary>显示名。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>是否启用；停用的使用者令牌仍在，但鉴权不通过。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>还能不能再加一名：已有 <paramref name="existing"/> 名时到了上限就抛出原来的读句。</summary>
    /// <param name="existing">此刻已有的使用者人数。</param>
    public static void EnsureRoomFor(int existing)
    {
        if (existing >= MaxUsers) throw new InvalidOperationException("最多支持 8 名使用者；轮换令牌不会占用新的名额。");
    }
}
