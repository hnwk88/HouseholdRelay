using Application.Contracts.Identity;
using Application.Interfaces.Identity;

namespace Application.Services.Identity;

/// <summary>
/// 服务进程里执行命令行交来的管理动作：与管理页走同一个 <see cref="IIdentityService"/>，提交后名单替换会同步通知思域会话层关闭受影响的会话，再返回结果。
/// 业务拒绝（<see cref="InvalidOperationException"/>）原样写进结果，不提交任何改动。
/// </summary>
public sealed class IdentityCommandHandler(IIdentityService identities)
{
    /// <summary>执行一个动作。</summary>
    public async Task<IdentityCommandResult> Execute(IdentityCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            switch (command.Action)
            {
                case IdentityCommandAction.Add:
                    return new(await identities.Create(command.DisplayName ?? command.Name ?? throw new InvalidOperationException("请提供 --display-name 或 --name。"), command.Name), null);
                case IdentityCommandAction.Rotate: return new(await identities.Rotate(User(command)), null);
                case IdentityCommandAction.Enable: await identities.SetEnabled(User(command), true); return new(null, null);
                case IdentityCommandAction.Disable: await identities.SetEnabled(User(command), false); return new(null, null);
                case IdentityCommandAction.Rename:
                    await identities.Rename(User(command), command.DisplayName ?? throw new InvalidOperationException("请提供 --display-name。"));
                    return new(null, null);
                default: throw new InvalidOperationException("clients 支持 list / add / rotate / enable / disable / rename。");
            }
        }
        // 业务拒绝的消息本身就是写给操作者的中文原因；事务已回滚，原样带回命令行。
        catch (InvalidOperationException error) { return new(null, error.Message); }
    }

    /// <summary>除新增外都要使用者主键。</summary>
    private static long User(IdentityCommand command) => command.UserId ?? throw new InvalidOperationException("请用 --id 指定使用者主键，或用 --name 查找旧客户端。");
}
