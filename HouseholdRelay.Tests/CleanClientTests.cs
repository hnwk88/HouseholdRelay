using System.Diagnostics;
using System.Text.Json;
using HouseholdRelay;
using Microsoft.AspNetCore.Http;
using static Tests;

/// <summary>在隔离客户端目录验证两个连接变量及真正的工具回合；本机 HTTP 不冒充可信公网 TLS。</summary>
static class CleanClientTests
{
    public static async Task ToolConversation()
    {
        await using var mock = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        var upstreams = new UpstreamService(web.Database); await mock.Activate(upstreams);
        var user = web.Identities.Create("干净客户端");
        using var temporary = new TestHome(); var inputPath = Path.Combine(temporary.Path, "fixture.txt");
        await File.WriteAllTextAsync(inputPath, "CLEAN_CLIENT_FILE_MARKER");
        mock.Handler = async context =>
        {
            var body = mock.Calls.Last().Body;
            if (!context.Request.Path.Value!.EndsWith("/messages", StringComparison.Ordinal)) { await UpstreamFixture.Success(context); return; }
            using var request = JsonDocument.Parse(body); var model = request.RootElement.GetProperty("model").GetString();
            var toolDone = body.Contains("CLEAN_CLIENT_FILE_MARKER", StringComparison.Ordinal);
            context.Response.ContentType = "text/event-stream";
            async Task Event(string type, object value) { await context.Response.WriteAsync($"event: {type}\ndata: {JsonSerializer.Serialize(value)}\n\n"); await context.Response.Body.FlushAsync(); }
            await Event("message_start", new { type = "message_start", message = new { id = "msg_fixture", type = "message", role = "assistant", model,
                content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null, usage = new { input_tokens = 12, output_tokens = 1 } } });
            await Event("ping", new { type = "ping" });
            if (!toolDone)
            {
                await Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "tool_use", id = "tool_clean", name = "Read", input = new { } } });
                await Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = JsonSerializer.Serialize(new { file_path = inputPath }) } });
            }
            else
            {
                await Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } });
                await Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "CLEAN_TOOL_OK" } });
            }
            await Event("content_block_stop", new { type = "content_block_stop", index = 0 });
            await Event("message_delta", new { type = "message_delta", delta = new { stop_reason = toolDone ? "end_turn" : "tool_use", stop_sequence = (string?)null }, usage = new { output_tokens = 4 } });
            await Event("message_stop", new { type = "message_stop" });
        };
        var start = new ProcessStartInfo(ClientLauncher.FindClaude())
        { WorkingDirectory = temporary.Path, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase)
            || key.Contains("PROXY", StringComparison.OrdinalIgnoreCase) || key is "NODE_EXTRA_CA_CERTS" or "SSL_CERT_FILE" or "NODE_TLS_REJECT_UNAUTHORIZED").ToArray()) start.Environment.Remove(key);
        start.Environment["ANTHROPIC_BASE_URL"] = web.Url; start.Environment["ANTHROPIC_AUTH_TOKEN"] = user.Token;
        start.Environment["CLAUDE_CONFIG_DIR"] = Path.Combine(temporary.Path, "isolated-config");
        start.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        foreach (var argument in new[] { "--safe-mode", "--no-session-persistence", "--strict-mcp-config", "--no-chrome", "--tools", "Read", "--allowedTools", "Read", "--model", "claude-sonnet-4-6", "--max-turns", "3", "--print", "Read the fixture file using the Read tool and report the result." }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new Exception("Could not start isolated Claude Code"); child.PriorityClass = ProcessPriorityClass.BelowNormal;
        child.StandardInput.Close(); var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)); await child.WaitForExitAsync(timeout.Token);
            Check(child.ExitCode == 0 && (await stdout).Contains("CLEAN_TOOL_OK"), "Isolated client failed its tool conversation: " + await stderr);
            Check(mock.Calls.Any(call => call.Body.Contains("tool_result") && call.Body.Contains("CLEAN_CLIENT_FILE_MARKER")));
            Check(mock.Calls.All(call => call.Headers.Values.All(value => !value.Contains(user.Token))));
            Console.WriteLine("Clean native client: " + await ClientLauncher.DetectUserAgent() + "; local HTTP, no CA file or legacy client bundle.");
        }
        finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
    }
}
