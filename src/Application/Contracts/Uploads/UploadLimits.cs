namespace Application.Contracts.Uploads;

/// <summary>管理页三种上传文件的大小上限，只在这里定义一次；页面按它限长读取，服务按它判定超限并读出原因。</summary>
public static class UploadLimits
{
    /// <summary>上游导出文件（加密信封）的上限：1 MB。</summary>
    public const int UpstreamExportBytes = 1024 * 1024;

    /// <summary>官方账户登录文件（.credentials.json）的上限：64 KB；Claude Code 写的凭据文件只有几 KB，超过的不是它。</summary>
    public const int OfficialCredentialBytes = 64 * 1024;

    /// <summary>可信证书 PFX 文件的上限：1 MB。</summary>
    public const int CertificateBytes = 1024 * 1024;
}
