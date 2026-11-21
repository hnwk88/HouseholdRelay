namespace Core.Configuration;

/// <summary>中转设置各整数项的取值范围，只在这里登记一次：<see cref="RelayConfiguration.Validate"/> 与设置页的 min、max 都取它。默认值在 <see cref="RelayConfiguration"/> 的属性初值里。</summary>
public static class SettingLimits
{
    /// <summary>中转监听端口。</summary>
    public static readonly SettingRange ListenPort = new(1024, 65535);

    /// <summary>上游最大并发。</summary>
    public static readonly SettingRange MaxConcurrent = new(1, 8);

    /// <summary>每人最大并发。</summary>
    public static readonly SettingRange MaxPerClient = new(1, 8);

    /// <summary>最大排队数。</summary>
    public static readonly SettingRange MaxQueued = new(1, 64);

    /// <summary>排队超时（秒）。</summary>
    public static readonly SettingRange QueueTimeoutSeconds = new(1, 120);

    /// <summary>上传超时（秒）。</summary>
    public static readonly SettingRange UploadTimeoutSeconds = new(1, 120);

    /// <summary>上游请求超时（秒）。</summary>
    public static readonly SettingRange RequestTimeoutSeconds = new(1, 3600);

    /// <summary>请求体上限（字节）。</summary>
    public static readonly SettingRange MaxBodyBytes = new(1, 67108864);

    /// <summary>每人每分钟请求上限。</summary>
    public static readonly SettingRange RequestsPerMinute = new(1, 10000);
}
