using System.IO.Pipes;
using Application.Contracts.Identity;
using Application.Interfaces.Configuration;
using Application.Services.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.LocalIpc;

/// <summary>
/// 服务进程里的本机管理通道：只接同一 Windows 用户、一次一个连接；收到一行动作就交给 <see cref="IdentityCommandHandler"/>（与管理页同一条路径）执行，
/// 执行完（已提交、受影响的思域会话已关闭）再回一行结果。只在宿主里作为后台服务运行，命令行进程不启动它。
/// </summary>
internal sealed class IdentityCommandPipeServer(IRelayInstance instance, IdentityCommandHandler handler, ILogger<IdentityCommandPipeServer> logger) : BackgroundService
{
    /// <summary>读请求的时限：连上后这么久还没写完一行就断开。</summary>
    private static readonly TimeSpan _requestWait = TimeSpan.FromSeconds(5);

    /// <summary>逐个接连接，直到服务停止。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var name = IdentityCommandPipe.Name(instance.State);
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
            }
            // 同名管道已被另一进程占着（同一数据目录的另一个实例）：本进程不提供通道，命令行会连到先启动的那个；只记一行类型。
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Identity command pipe unavailable: {Type}", error.GetType().Name);
                return;
            }
            await using (pipe)
            {
                try { await pipe.WaitForConnectionAsync(stoppingToken); }
                // 服务停止：不再接新连接。
                catch (OperationCanceledException) { return; }
                await ServeAsync(pipe, stoppingToken);
            }
        }
    }

    /// <summary>读一行动作、执行、回一行结果；坏请求回固定原因，不提交任何改动。连接断开等传输故障只结束这一个连接。</summary>
    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stopping)
    {
        try
        {
            // 服务停止与读请求时限合成一个令牌，只约束读请求，不约束随后的执行。
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            wait.CancelAfter(_requestWait);
            var command = await IdentityCommandPipe.ReadLineAsync<IdentityCommand>(pipe, wait.Token);
            // 执行不受读请求时限约束：已开始的提交要做完，结果照实回给命令行。
            var result = command is null ? new IdentityCommandResult(null, "命令行发来的管理请求无法读懂，没有保存。请用同一版本的程序重试。")
                : await handler.ExecuteAsync(command);
            await IdentityCommandPipe.WriteLineAsync(pipe, result, stopping);
        }
        // 命令行中途断开、读超时或服务停止：只结束这个连接，接下一个；不影响服务。
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            logger.LogInformation("Identity command connection ended early: {Type}", error.GetType().Name);
        }
    }
}
