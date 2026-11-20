using Repositories;

namespace Core.Configuration;

/// <summary>一次站点设备维护操作的幂等记录（表 site_identity_operations）：操作编号（唯一，32 位十六进制）、它提交的身份代次（唯一）、是否尚未完成。不保存历史设备编号。</summary>
public sealed class SiteIdentityOperation : BaseEntity
{
    /// <summary>操作编号，页面生成，统一成 32 位小写十六进制。</summary>
    public string OperationId { get; set; } = "";

    /// <summary>这次操作提交的身份代次（从 2 开始）。</summary>
    public long Version { get; set; }

    /// <summary>派生文件还没对齐完、尚未完成。</summary>
    public bool Pending { get; set; }
}
