namespace Core.Configuration;

/// <summary>站点设备编号的格式规则；不从配置缺失或异常中推导新编号。</summary>
public static class SiteDeviceIdentity
{
    /// <summary>接受恰好 64 个十六进制字符，保留已有合法编号的大小写。</summary>
    public static bool IsValid(string? value) => RelayConfiguration.IsHex(value, 64);
}
