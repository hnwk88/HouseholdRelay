using Core.Identity;

namespace Application.Contracts.Identity;

/// <summary>
/// 运行中服务执行管理动作的结果：成功时 <see cref="Error"/> 为 null，新增与轮换带一次签发的令牌；失败时 <see cref="Error"/> 是读给操作者的原因（没有提交任何改动）。
/// </summary>
public sealed record IdentityCommandResult(IssuedToken? Issued, string? Error);
