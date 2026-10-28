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
                                .Select(item => item.GetProperty("id").GetString()!).Where(model => SafeModel(model, snapshot.Credential!)).Distinct().Take(100).ToArray();
                            discovery = "passed"; authentication = "passed";
                        }
                        else discovery = "unsupported";
                    }
                    catch (JsonException) { discovery = "unsupported"; }
                }
                else discovery = "failed";
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { model = snapshot.Options.Model, max_tokens = 16, stream = false,
                messages = new[] { new { role = "user", content = "Reply with OK." } } });
            using var call = transport.Request("/v1/messages", "", bytes, "POST", "HouseholdRelay/1.0");
            call.Content!.Headers.ContentType = new("application/json");
            using var result = await transport.Send(call, timeout.Token);
            if (result.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            { authentication = "reconnect"; return Result("failed", "最小模型调用的凭据被拒绝，请重新接入。"); }
            if (result.StatusCode == HttpStatusCode.TooManyRequests) return Result("cooldown", "最小模型调用受到上游限流，请稍后重新测试。");
            if (!result.IsSuccessStatusCode) return Result("failed", $"最小模型调用返回 HTTP {(int)result.StatusCode}，请检查模型权限和上游配置。");
            await using var content = await result.Content.ReadAsStreamAsync(timeout.Token);
            using var message = JsonDocument.Parse(await UpstreamResponses.ReadBounded(content, 1024 * 1024, timeout.Token));
            var root = message.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "message"
                || !root.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String || role.GetString() != "assistant"
                || !root.TryGetProperty("id", out var messageId) || messageId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(messageId.GetString())
                || !root.TryGetProperty("content", out var blocks) || blocks.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("model", out var returnedModel) || returnedModel.ValueKind != JsonValueKind.String || !SafeModel(returnedModel.GetString()!, snapshot.Credential!))
                return Result("failed", "上游未返回有效的 Anthropic Messages 回复，不能标记调用成功。");
            verifiedModel = returnedModel.GetString(); authentication = "passed";
            return Result("passed", "最小模型调用成功。此检测为管理员诊断，不计入个人业务用量；凭据到期时间未知。");
        }
        catch (OperationCanceledException) { return Result(cancellation.IsCancellationRequested ? "cancelled" : "failed", "检测已取消或超时，候选未激活。"); }
        catch (HttpRequestException) { connection = "failed"; return Result("failed", "连接或 TLS 校验失败，请检查地址、端口、证书及代理范围。"); }
        catch (RelayFailure error) { return Result("failed", error.Status == 502 ? "上游返回跳转、过大或不支持的响应，候选未激活。" : "上游检测失败。"); }
        catch (JsonException) { return Result("failed", "最小调用返回的 JSON 无效，候选未激活。"); }
        catch (IOException) { return Result("failed", "上游响应中断，候选未激活。"); }
    }

    private static bool SafeModel(string model, string secret) => model.Length is > 0 and <= 200
        && model.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':') && UpstreamResponses.Redact(model, secret) == model;
}
