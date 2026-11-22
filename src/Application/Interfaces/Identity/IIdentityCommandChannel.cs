using Application.Contracts.Identity;

namespace Application.Interfaces.Identity;

/// <summary>
/// 命令行到运行中服务的本机管理通道：服务在运行时，使用者管理动作必须经它交给服务执行（服务提交、关闭受影响的思域会话后才回确认），
/// 不能由命令行直接落库再事后通知。只本机、只同一 Windows 用户可连。
/// </summary>
public interface IIdentityCommandChannel
{
    /// <summary>
    /// 把动作交给运行中的服务并等它回结果。连不上、超时或回应无法读懂时抛 <see cref="InvalidOperationException"/>（读给操作者的原因与下一步），此时服务没有确认执行。
    /// </summary>
    Task<IdentityCommandResult> Send(IdentityCommand command, CancellationToken cancellation);
}
