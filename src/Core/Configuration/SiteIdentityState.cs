using Repositories;

namespace Core.Configuration;

/// <summary>
/// 站点身份的当前状态（表 site_identity，主键固定为 1 的单行）：身份代次、最近一次维护操作（按主键关联 <see cref="SiteIdentityOperation"/>）、是否有未完成的维护。
/// 设备编号本身只有设置里的一份，这里不另存。
/// </summary>
public sealed class SiteIdentityState : BaseEntity
{
    /// <summary>单行表唯一那一行的主键。</summary>
    public const long SingletonId = 1;

    /// <summary>身份代次，从 1 开始，每换一次号加一。</summary>
    public long Version { get; set; }

    /// <summary>最近一次维护操作的主键；从没维护过为空。</summary>
    public long? SiteIdentityOperationId { get; set; }

    /// <summary>有没有未完成的维护。</summary>
    public bool Pending { get; set; }
}
