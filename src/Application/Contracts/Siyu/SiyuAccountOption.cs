namespace Application.Contracts.Siyu;

/// <summary>思域可选的一个官方账户：上游主键与显示名称。名称只用来显示，建会话只认主键；不含凭据、Uid、account_uuid、邮箱。</summary>
public sealed record SiyuAccountOption(long OfficialAccountId, string DisplayName);
