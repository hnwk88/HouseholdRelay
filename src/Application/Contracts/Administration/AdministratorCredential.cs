namespace Application.Contracts.Administration;

/// <summary>唯一管理员的主键与密码哈希，只在验证登录时短暂读取。</summary>
public sealed record AdministratorCredential(long Id, string PasswordHash);
