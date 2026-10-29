namespace Core.Configuration;

/// <summary>带乐观并发版本号的设置快照；保存时用版本号拒绝过期表单。</summary>
public sealed record SettingsSnapshot(long Version, RelayConfiguration Value);
