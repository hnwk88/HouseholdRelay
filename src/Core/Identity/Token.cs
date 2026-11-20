using Repositories;

namespace Core.Identity;

/// <summary>
/// 一版个人令牌（表 tokens）：属于哪名使用者、第几版、令牌哈希（唯一）、撤销时刻。每名使用者同一时刻只有一版未撤销（库里唯一索引兜底）。
/// 轮换 = 撤销当前版（<see cref="Revoke"/>）+ 写入下一版。
/// </summary>
public sealed class Token : BaseEntity
{
    /// <summary>令牌属于的使用者主键。</summary>
    public long UserId { get; set; }

    /// <summary>这名使用者的第几版令牌，从 1 开始。</summary>
    public int Version { get; set; }

    /// <summary>令牌的十六进制哈希；明文只在签发那一次返回。</summary>
    public string Hash { get; set; } = "";

    /// <summary>撤销时刻（ISO 8601 往返格式文本，沿用旧库的写法）；仍有效时为空。</summary>
    public string? RevokedAt { get; set; }

    /// <summary>撤销这一版令牌；已撤销的保留原时刻。</summary>
    /// <param name="now">此刻。</param>
    public void Revoke(DateTimeOffset now) => RevokedAt ??= now.ToString("O");
}
