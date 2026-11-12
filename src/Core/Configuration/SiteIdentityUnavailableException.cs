namespace Core.Configuration;

/// <summary>站点维护或派生配置未就绪导致的本地拒绝，不属于上游认证失败。</summary>
public sealed class SiteIdentityUnavailableException : InvalidOperationException
{
    /// <summary>固定的用户读句，不带设备值、路径或凭据。</summary>
    public SiteIdentityUnavailableException() : base("官方账户暂不可用：站点设备准备或维护尚未完成。请在本机管理页查看设备维护状态；独立 API 上游仍可使用。") { }
}
