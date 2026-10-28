using System.Net;
using System.Text.Json;

namespace HouseholdRelay;

/// <summary>独立诊断候选配置，仅完成有效 Messages 调用后允许激活。</summary>
internal static class UpstreamProbe
{
    internal static async Task<UpstreamReport> Run(UpstreamSnapshot snapshot, CancellationToken cancellation)
    {
        var connection = "unverified"; var authentication = "unverified"; var discovery = "unverified";
        var models = Array.Empty<string>(); string? verifiedModel = null; var checkedAt = DateTimeOffset.UtcNow;
        UpstreamReport Result(string call, string message) => new(checkedAt, connection, authentication, discovery, call, models, verifiedModel, message);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var transport = new UpstreamConnection(snapshot, 1);
            using (var request = transport.Request("/v1/models", "?limit=100", [], "GET", "HouseholdRelay/1.0"))
            using (var response = await transport.Send(request, timeout.Token))
            {
                connection = "passed";
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                { authentication = "reconnect"; return Result("failed", "上游拒绝了凭据，请重新填写并测试。"); }
                if (response.StatusCode == HttpStatusCode.TooManyRequests) return Result("cooldown", "上游请求过多；诊断没有改变活动配置的冷却状态。");
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) discovery = "unsupported";
                else if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    try
                    {
                        using var body = JsonDocument.Parse(await UpstreamResponses.ReadBounded(stream, 1024 * 1024, timeout.Token));
                        if (body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        {
                            models = data.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                                .Select(item => item.GetProperty("id").GetString()!).Where(model => ProbeMessage.SafeModel(model, snapshot.Credential!)).Distinct().Take(100).ToArray();
                            discovery = "passed"; authentication = "passed";
                        }
                        else discovery = "unsupported";
                    }
                    catch (JsonException) { discovery = "unsupported"; }
                }
                else discovery = "failed";
            }
            var result = snapshot.Options.Diagnostic == "claude-code" ? await ClaudeCodeProbe.Run(transport, timeout.Token)
                : await StandardCall(transport, timeout.Token);
            if (result.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            { authentication = "reconnect"; return Result("failed", "最小模型调用的凭据被拒绝，请重新接入。"); }
            if (result.Status == HttpStatusCode.TooManyRequests) return Result("cooldown", "最小模型调用受到上游限流，请稍后重新测试。");
            if ((int)result.Status is < 200 or >= 300) return Result("failed", $"最小模型调用返回 HTTP {(int)result.Status}，请检查模型权限和上游配置。若仅 Claude Code 能调用，可选择实际客户端检测；本次未自动重试。");
            verifiedModel = ProbeMessage.Read(result.Body, result.Streaming, snapshot.Credential!);
            if (verifiedModel is null)
                return Result("failed", "上游未返回有效的 Anthropic Messages 回复，不能标记调用成功。");
            authentication = "passed";
            return Result("passed", "最小模型调用成功。" + (snapshot.Options.Diagnostic == "claude-code" ? "已验证 Claude Code 实际会话；通用 API 请求是否被接受取决于上游。" : "")
                + "此检测为管理员诊断，不计入个人业务用量；凭据到期时间未知。");
        }
        catch (OperationCanceledException) { return Result(cancellation.IsCancellationRequested ? "cancelled" : "failed", "检测已取消或超时，候选未激活。"); }
        catch (HttpRequestException) { connection = "failed"; return Result("failed", "连接或 TLS 校验失败，请检查地址、端口、证书及代理范围。"); }
        catch (RelayFailure error) { return Result("failed", error.Status == 502 ? "上游返回跳转、过大或不支持的响应，候选未激活。" : "上游检测失败。"); }
        catch (JsonException) { return Result("failed", "最小调用返回的 JSON 无效，候选未激活。"); }
        catch (IOException) { return Result("failed", "上游响应中断，候选未激活。"); }
        catch (InvalidOperationException) { return Result("failed", "Claude Code 检测未完成，请确认本机已安装可用的原生客户端；普通 API 可选择标准 Messages 检测。候选未激活。"); }
        catch (System.ComponentModel.Win32Exception) { return Result("failed", "无法启动本机 Claude Code，候选未激活。"); }
    }

    private static async Task<ClaudeCodeProbe.Reply> StandardCall(UpstreamConnection transport, CancellationToken cancellation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { model = transport.Snapshot.Options.Model, max_tokens = 16, stream = false,
            messages = new[] { new { role = "user", content = "Reply with OK." } } });
        using var call = transport.Request("/v1/messages", "", bytes, "POST", "HouseholdRelay/1.0");
        call.Content!.Headers.ContentType = new("application/json");
        using var result = await transport.Send(call, cancellation);
        await using var content = await result.Content.ReadAsStreamAsync(cancellation);
        return new(result.StatusCode, await UpstreamResponses.ReadBounded(content, 1024 * 1024, cancellation), result.Content.Headers.ContentType?.MediaType == "text/event-stream");
    }
}
