using System.Globalization;

namespace Core.Identity;

/// <summary>一名使用者当前有效令牌的鉴权视图，只含令牌哈希，不含明文；<see cref="UserId"/>、<see cref="TokenId"/> 是库里的整数主键，旧内存夹具没有库时为 null。</summary>
public sealed record ClientAccess(string Name, string TokenHash, bool Enabled = true, long? UserId = null, long? TokenId = null)
{
    // 旧内存夹具兼容名称；所有数据库身份使用不可变主键。
    /// <summary>用于并发分组和限流的稳定身份键（只在内存里用，不落库）：有数据库用户主键时用它的十进制文本，旧内存夹具没有主键时退回 <see cref="Name"/>。</summary>
    public string Id => UserId?.ToString(CultureInfo.InvariantCulture) ?? Name;
}
