using System.IO.Pipes;
using Application.Contracts.Identity;
using Application.Interfaces.Configuration;
using Application.Interfaces.Identity;

namespace Infrastructure.LocalIpc;

/// <summary>命令行一侧的本机管理通道：连上运行中服务的管道（只同一 Windows 用户），发一行动作，等一行结果。</summary>
internal sealed class IdentityCommandPipeClient(IRelayInstance instance) : IIdentityCommandChannel
{
    /// <summary>连接时限。</summary>
    private static readonly TimeSpan _connectWait = TimeSpan.FromSeconds(5);

    /// <summary>等结果的时限：服务要提交一次写入并关闭会话，正常远小于它。</summary>
    private static readonly TimeSpan _replyWait = TimeSpan.FromSeconds(60);

    /// <summary>发动作并等结果；连不上、超时或回应读不懂都抛出读给操作者的原因。</summary>
    public async Task<IdentityCommandResult> Send(IdentityCommand command, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(command);
        // 不用 CurrentUserOnly：它拿进程令牌的「所有者」比对管道所有者，提权控制台（同一用户）的令牌所有者是 Administrators 组，会被误判成别人。
        // 改为连上后自己核对管道所有者是不是本人（见 OwnedByCurrentUser）；服务端照旧以 CurrentUserOnly 建管道，访问控制只授予这个用户。
        await using var pipe = new NamedPipeClientStream(".", IdentityCommandPipe.Name(instance.State), PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(_connectWait, cancellation);
            if (!OwnedByCurrentUser(pipe)) throw new UnauthorizedAccessException();
        }
        // 服务在运行（占着服务锁）却连不上它的通道，或通道不是本人建的：多半是另一个 Windows 用户启动的服务。不保存，说清下一步。
        catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("中转服务正在运行，但连不上它的本机管理通道，这次没有保存。请用启动服务的同一个 Windows 用户执行，或在管理页里操作。");
        }
        // 调用方取消与等结果的时限合成一个令牌；两者靠调用方令牌是否已取消来区分读法。
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        wait.CancelAfter(_replyWait);
        IdentityCommandResult? result;
        try
        {
            await IdentityCommandPipe.WriteLine(pipe, command, wait.Token);
            result = await IdentityCommandPipe.ReadLine<IdentityCommandResult>(pipe, wait.Token);
        }
        // 发出后没等到回应：服务可能已执行也可能没有，照实说不确定，叫操作者去核对。
        catch (Exception error) when (error is IOException or OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException("已把请求交给运行中的中转服务，但没有等到确认，不确定是否已保存。请用 clients list 或管理页核对后再决定是否重试。");
        }
        return result ?? throw new InvalidOperationException("已把请求交给运行中的中转服务，但回应无法读懂，不确定是否已保存。请用 clients list 或管理页核对。");
    }

    /// <summary>
    /// 管道的所有者是不是本人：等于当前用户的 SID（服务以普通权限启动时），或等于当前令牌的所有者（服务与命令行都在提权控制台里时）才算。
    /// 防的是别的用户抢先建同名管道冒充服务。
    /// </summary>
    private static bool OwnedByCurrentUser(NamedPipeClientStream pipe)
    {
        using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
        var owner = pipe.GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier));
        return owner is not null && (owner.Equals(current.User) || owner.Equals(current.Owner));
    }
}
