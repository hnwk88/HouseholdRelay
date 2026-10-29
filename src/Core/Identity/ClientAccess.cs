namespace Core.Identity;

/// <summary>一名使用者当前有效令牌的鉴权视图，只含令牌哈希，不含明文。</summary>
public sealed record ClientAccess(string Name, string TokenHash, bool Enabled = true, string? UserId = null, string? TokenId = null)
{
    // 旧内存夹具兼容名称；所有数据库身份使用不可变主键。
    /// <summary>用于并发分组、限流和用量归属的稳定身份键：有数据库用户主键时用 <see cref="UserId"/>，旧内存夹具没有主键时退回 <see cref="Name"/>。</summary>
    public string Id => UserId ?? Name;
}
