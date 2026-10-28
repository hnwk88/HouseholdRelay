using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace HouseholdRelay;

/// <summary>可选的隔离原生客户端诊断；只允许一次候选生成请求，真实凭据只留在中转进程。</summary>
internal static class ClaudeCodeProbe
{
    internal sealed record Reply(HttpStatusCode Status, byte[] Body, bool Streaming);

    internal static async Task<Reply> Run(UpstreamConnection transport, CancellationToken cancellation)
    {
        var executable = ClientLauncher.FindClaude();
        var parent = Path.GetFullPath(Path.GetTempPath());
        var directory = Path.GetFullPath(Path.Combine(parent, "HouseholdRelay-diagnostic-" + Guid.NewGuid().ToString("N")));
        WindowsSecrets.ProtectDirectory(directory);
        var localToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var completion = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously); var dispatched = 0;
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
            try
            {
                var body = await UpstreamResponses.ReadBounded(context.Request.Body, 1024 * 1024, cancellation);
                using var request = transport.Request("/v1/messages", context.Request.QueryString.ToString(), body, "POST",
                    context.Request.Headers.UserAgent.ToString(), context.Request.Headers);
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
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or RelayFailure)
            { completion.TrySetException(error); context.Abort(); }
        });
        Process? child = null;
        try
        {
            await app.StartAsync(cancellation);
            var start = Start(executable, directory, app.Urls.Single(), localToken, transport.Snapshot.Options.Model);
            child = Process.Start(start) ?? throw new InvalidOperationException("无法启动隔离的 Claude Code 检测。");
            child.PriorityClass = ProcessPriorityClass.BelowNormal; child.StandardInput.Close();
            var output = child.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellation);
            var errors = child.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellation);
            var exited = child.WaitForExitAsync(cancellation);
            var finished = await Task.WhenAny(completion.Task, exited);
            cancellation.ThrowIfCancellationRequested();
            if (finished == exited && !completion.Task.IsCompleted)
                throw new InvalidOperationException("Claude Code 未发送检测请求；请检查本机客户端版本，或选择标准 Messages 检测。");
            var reply = await completion.Task.WaitAsync(cancellation);
            if ((int)reply.Status is >= 200 and < 300)
            {
                await exited; await Task.WhenAll(output, errors);
                if (child.ExitCode != 0) throw new InvalidOperationException("Claude Code 未完成检测会话，候选未激活。");
            }
            return reply;
        }
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
                try { Directory.Delete(directory, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static ProcessStartInfo Start(string executable, string directory, string url, string token, string model)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase) || key.Contains("PROXY", StringComparison.OrdinalIgnoreCase)
            || key is "NODE_EXTRA_CA_CERTS" or "SSL_CERT_FILE" or "NODE_TLS_REJECT_UNAUTHORIZED").ToArray()) start.Environment.Remove(key);
        start.Environment["ANTHROPIC_BASE_URL"] = url; start.Environment["ANTHROPIC_AUTH_TOKEN"] = token;
        start.Environment["CLAUDE_CONFIG_DIR"] = Path.Combine(directory, "config"); start.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        foreach (var argument in new[] { "--safe-mode", "--no-session-persistence", "--strict-mcp-config", "--no-chrome", "--tools", "",
            "--setting-sources", "", "--model", model, "--max-turns", "1", "--output-format", "json", "--print", "Reply with OK." }) start.ArgumentList.Add(argument);
        return start;
    }
}
