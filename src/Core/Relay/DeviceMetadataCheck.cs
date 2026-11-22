namespace Core.Relay;

/// <summary>只读核对 Messages 正文 <c>metadata.user_id</c> 的结论：不改任何字节，只说两段是否与目标一致。</summary>
public enum DeviceMetadataCheck
{
    /// <summary>设备段与账户段都等于目标。</summary>
    Match,

    /// <summary>设备段不是站点设备编号。</summary>
    DeviceDiffers,

    /// <summary>设备段对，账户段不是这个官方账户的编号（含空串、缺失）。</summary>
    AccountDiffers,

    /// <summary>没有 metadata、重复、损坏或格式不认识。</summary>
    Unsupported,
}
