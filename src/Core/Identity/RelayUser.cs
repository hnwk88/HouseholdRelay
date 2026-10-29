namespace Core.Identity;

/// <summary>管理页面展示的使用者及其当前令牌版本。</summary>
public sealed record RelayUser(string Id, string Name, string DisplayName, bool Enabled, int TokenVersion);
