using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Application.Contracts.Upstream;
using Application.Interfaces.ClaudeCode;
using Application.Interfaces.Security;
using Application.Interfaces.Upstream;
using Core.Relay;
using Core.Upstream;
using Infrastructure.ExternalSources.Upstream;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Infrastructure.ExternalSources.ClaudeCode;

/// <summary>可选的隔离原生客户端诊断；只允许一次候选生成请求，真实凭据只留在中转进程。</summary>
internal sealed class ClaudeCodeProbe(IClientLauncher launcher, ISecretProtector protector, IUpstreamProxy proxy) : IClaudeCodeProbe
{
    /// <summary>本机能否找到原生 claude.exe；找不到时返回 false，不抛异常。</summary>
    public bool IsAvailable
    {
        get
        {
            try { launcher.FindClaude(); return true; }
            // 定位失败只说明本机没装，交给调用方决定是否改用实际客户端测试。
            catch (InvalidOperationException) { return false; }
        }
    }

    /// <summary>
    /// 经本机临时端点让隔离的 Claude Code 发出唯一一次候选请求并转发给上游；发往官方账户时只带 <paramref name="identity"/> 这份统一身份。
    /// 代理已启用却取不到地址时在启动子进程前抛出 <see cref="Core.Upstream.UpstreamProxyUnavailableException"/>。
    /// </summary>
    public Task<ProbeReply> Run(IUpstreamLease transport, ClaudeCodeIdentity? identity, CancellationToken cancellation) =>
        Session<ProbeReply>(transport.Snapshot.Options.Model, new Uri(transport.Snapshot.Options.BaseUrl), async (context, completion) =>
        {
            try
            {
                var body = await UpstreamResponses.ReadBounded(context.Request.Body, 1024 * 1024, cancellation);
                using var request = transport.Request("/v1/messages", context.Request.QueryString.ToString(), body, "POST",
                    context.Request.Headers.UserAgent.ToString(), context.Request.Headers, identity);
                using var response = await transport.Send(request, cancellation);
                await using var source = await response.Content.ReadAsStreamAsync(cancellation);
                var bytes = await UpstreamResponses.ReadBounded(source, 1024 * 1024, cancellation);
                var streaming = response.Content.Headers.ContentType?.MediaType == "text/event-stream";
                // 用已有响应边界返回给本机客户端，防止错误正文中的上游秘密进入客户端临时文件。
                using var copy = new HttpResponseMessage(response.StatusCode) { Content = new ByteArrayContent(bytes) };
                copy.Content.Headers.ContentType = response.Content.Headers.ContentType;
                foreach (var encoding in response.Content.Headers.ContentEncoding) copy.Content.Headers.ContentEncoding.Add(encoding);
                await UpstreamResponses.Copy(copy, context.Response, transport.Snapshot.Credential!, "diagnostic", cancellation);
                completion.TrySetResult(new(response.StatusCode, bytes, streaming));
            }
            // 只接住网络、读写、取消、中转失败和统一身份缺失：把原因交给等待方，并中断本机连接，避免客户端拿到半截响应后继续重试。
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or RelayFailure or ClaudeCodeIdentityUnavailableException)
            { completion.TrySetException(error); context.Abort(); }
        }, reply => (int)reply.Status is >= 200 and < 300, cancellation);

    /// <summary>
    /// 让隔离的 Claude Code 向本机临时端点发一次请求，只记下它带的请求头（去掉认证头）就回 400 结束会话；不转发、不碰任何上游，不计费。
    /// 子进程的代理变量按官方地址的全局代理设置写，与登录、续期的子进程是同一个环境。
    /// </summary>
    public Task<IReadOnlyList<KeyValuePair<string, string>>> Capture(CancellationToken cancellation) =>
        Session<IReadOnlyList<KeyValuePair<string, string>>>(null, OfficialAccountLogin.OfficialTarget, (context, completion) =>
        {
            completion.TrySetResult([.. context.Request.Headers.Where(header => !header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                .SelectMany(header => header.Value.Select(value => KeyValuePair.Create(header.Key, value ?? "")))]);
            context.Response.StatusCode = 400; return Task.CompletedTask;
        }, _ => false, cancellation);

    /// <summary>
    /// 起一个只认一次性本地令牌的本机临时端点，再以隔离环境启动 Claude Code 指向它；第一个合格请求交给 <paramref name="handle"/>，之后的一律 429，SDK 重试也不会多发。
    /// 等到 <paramref name="handle"/> 给出结果就返回；<paramref name="settle"/> 对结果为真时还要等子进程正常退出并排空输出，确认客户端完整走完一次会话。
    /// 先找 claude.exe 再按 <paramref name="target"/> 解析代理，两步失败都在启动子进程之前抛出。
    /// </summary>
    private async Task<T> Session<T>(string? model, Uri target, Func<HttpContext, TaskCompletionSource<T>, Task> handle, Func<T, bool> settle, CancellationToken cancellation)
    {
        var executable = launcher.FindClaude();
        var route = proxy.Resolve(target);
        var parent = Path.GetFullPath(Path.GetTempPath());
        var directory = Path.GetFullPath(Path.Combine(parent, "HouseholdRelay-diagnostic-" + Guid.NewGuid().ToString("N")));
        protector.ProtectDirectory(directory);
        var localToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously); var dispatched = 0;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = directory });
        builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.Run(async context =>
        {
            if (context.Request.Method != "POST" || context.Request.Path != "/v1/messages"
                || context.Request.Headers.Authorization != "Bearer " + localToken)
            { context.Response.StatusCode = 404; return; }
            // SDK 降级或重试也不能产生第二次计费请求。
            if (Interlocked.CompareExchange(ref dispatched, 1, 0) != 0) { context.Response.StatusCode = 429; return; }
            await handle(context, completion);
        });
        Process? child = null;
        try
        {
            await app.StartAsync(cancellation);
            var start = Start(executable, directory, app.Urls.Single(), localToken, model, route);
            child = Process.Start(start) ?? throw new InvalidOperationException("无法启动隔离的 Claude Code 检测。");
            child.PriorityClass = ProcessPriorityClass.BelowNormal; child.StandardInput.Close();
            var output = child.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellation);
            var errors = child.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellation);
            var exited = child.WaitForExitAsync(cancellation);
            // 同时等候选请求完成和子进程退出：子进程没发请求就退出时立即报错，不必干等到取消。
            var finished = await Task.WhenAny(completion.Task, exited);
            cancellation.ThrowIfCancellationRequested();
            if (finished == exited && !completion.Task.IsCompleted)
                throw new InvalidOperationException("Claude Code 未发送检测请求；请检查本机客户端版本，或选择标准 Messages 检测。");
            var reply = await completion.Task.WaitAsync(cancellation);
            if (settle(reply))
            {
                // 上游成功时还要等子进程正常收尾并排空输出管道，确认客户端完整走完一次会话才算检测通过。
                await exited; await Task.WhenAll(output, errors);
                if (child.ExitCode != 0) throw new InvalidOperationException("Claude Code 未完成检测会话，候选未激活。");
            }
            return reply;
        }
        // 无论成败都结束子进程树、停掉临时端点并删除隔离目录，防止残留进程或临时配置留在本机。
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); }
                child.Dispose();
            }
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await app.StopAsync(stop.Token);
            if (directory.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                // 只在确认目录位于系统临时目录之下时递归删除，避免路径异常时误删别处文件。
                try { Directory.Delete(directory, true); }
                // 文件仍被占用时放弃清理：残留的只是临时目录，不应让检测结果因此失败。
                catch (IOException) { }
                // 没有删除权限时同样放弃清理，保留检测本身的结果。
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// 组装隔离检测用的子进程启动参数：清掉继承的 Anthropic、Claude 和证书相关环境变量，代理变量先清后按全局设置写入，只指向本机临时端点和一次性本地令牌，并以安全模式、单轮、无工具方式运行；
    /// <paramref name="model"/> 为 null 时不传 <c>--model</c>（只抓请求头时用它自己的默认模型）。
    /// </summary>
    internal static ProcessStartInfo Start(string executable, string directory, string url, string token, string? model, Core.Upstream.ProxyRoute route)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        IsolatedClaudeEnvironment.Apply(start, Path.Combine(directory, "config"), route);
        start.Environment["ANTHROPIC_BASE_URL"] = url; start.Environment["ANTHROPIC_AUTH_TOKEN"] = token;
        string[] chosen = model is null ? [] : ["--model", model];
        string[] arguments = ["--safe-mode", "--no-session-persistence", "--strict-mcp-config", "--no-chrome", "--tools", "",
            "--setting-sources", "", .. chosen, "--max-turns", "1", "--output-format", "json", "--print", "Reply with OK."];
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }
}
