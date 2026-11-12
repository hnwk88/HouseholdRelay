namespace Core.Configuration;

/// <summary>当前唯一设备编号和持久身份代次；未完成状态禁止新的官方操作。</summary>
public sealed record SiteIdentitySnapshot(string DeviceId, long Version, string? OperationId, bool Pending);
