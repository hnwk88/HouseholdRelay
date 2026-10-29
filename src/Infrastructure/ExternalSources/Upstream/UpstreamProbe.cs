using System.Net;
using System.Text.Json;
using Application.Contracts.Upstream;
using Application.Interfaces.Upstream;
using Core.Relay;
using Core.Upstream;

namespace Infrastructure.ExternalSources.Upstream;

/// <summary>独立诊断已保存上游：读模型目录时探测认证方式，最小调用被拒时按需改用 Claude Code；仅完成有效 Messages 调用后允许切换。</summary>
internal sealed class UpstreamProbe(IClaudeCodeProbe claudeCode, IUpstreamProxy proxy) : IUpstreamProbe
{
    /// <summary>只读模型目录（30 秒上限），按 <see cref="UpstreamDetection.AuthenticationOrder"/> 逐个尝试认证方式，把结果读成一句话；网络、代理和协议失败都折成 <c>failed</c>，不抛给页面。</summary>
    public async Task<ModelCatalog> Discover(UpstreamSnapshot snapshot, CancellationToken cancellation)
    {
        using var timeout = Limit(cancellation, 30);
        var options = snapshot.Options;
        try
        {
            var catalog = await Catalog(snapshot, UpstreamDetection.AuthenticationOrder(options), timeout.Token);
            var manual = options.AuthenticationManual && options.Mode != "oauth" ? "（手动指定）" : "";
            return catalog.Status switch
            {
                "passed" when catalog.Models.Length > 0 => catalog with { Message = $"已获取 {catalog.Models.Length} 个模型，认证用 {UpstreamDetection.AuthenticationLabel(catalog.Authentication!)}{manual}。请在下面选择模型，再按测试。" },
                "passed" => catalog with { Message = $"上游接受了密钥（认证用 {UpstreamDetection.AuthenticationLabel(catalog.Authentication!)}{manual}），但模型目录是空的，请手填模型名。" },
                "unsupported" => catalog with { Message = "上游不提供模型目录，请手填模型名。" },
                "rejected" => catalog with { Message = UpstreamDetection.Rejected(options) },
                "cooldown" => catalog with { Message = "上游请求过多，请稍后再获取模型。" },
                _ when catalog.Message.StartsWith('5') => catalog with { Message = $"获取模型失败，上游返回 HTTP {catalog.Message}。{Unavailable()}" },
                _ => catalog with { Message = $"获取模型失败，上游返回 HTTP {catalog.Message}。请检查地址是否填对。" }
            };
        }
        // 区分管理员主动取消与超时，都只读出原因、不抛给页面。
        catch (OperationCanceledException) { return Failed(cancellation.IsCancellationRequested ? "获取模型已取消。" : "获取模型超时（30 秒），请检查网络和代理后重试。"); }
        // 建连、TLS 或代理失败：读出代理原因或固定说明，不回显底层细节。
        catch (HttpRequestException error) { return Failed(ConnectionMessage(error)); }
        // 中转自身拒绝（重定向、响应超限）：固定说明。
        catch (RelayFailure) { return Failed("上游返回跳转、过大或不支持的响应，没有取得模型目录。"); }
        // 读取目录时连接中断。
        catch (IOException) { return Failed("上游响应中断，没有取得模型目录，请重试。"); }
    }

    /// <summary>
    /// 诊断一次（60 秒上限，可能跑两次 Claude Code）：先读模型目录确定认证方式，再按认证顺序做最小调用；
    /// 检测方式自动时先发标准 Messages，非 2xx 且不是 401、429 时，本机装有 Claude Code 就改用实际客户端再测；手动指定的认证方式和检测方式只用那一种。
    /// 返回不含秘密的报告与实际采用的配置。
    /// </summary>
    public async Task<ProbeResult> Run(UpstreamSnapshot snapshot, CancellationToken cancellation)
    {
        var options = snapshot.Options; var used = options;
        var connection = "unverified"; var authentication = "unverified"; var discovery = "unverified";
        var models = Array.Empty<string>(); string? verifiedModel = null; var checkedAt = DateTimeOffset.UtcNow;
        ProbeResult Result(string call, string message) => new(new(checkedAt, connection, authentication, discovery, call, models, verifiedModel, message), used);
        // 链接调用方的取消并另加上限：管理员取消或上游无响应时都能及时结束检测，后续所有网络和子进程操作都用这个令牌。
        using var timeout = Limit(cancellation, 60);
        try
        {
            var order = UpstreamDetection.AuthenticationOrder(options);
            // 获取模型已确认的认证方式排在最前，其余按固定顺序在后。
            order = [.. order.Where(item => item == options.Authentication), .. order.Where(item => item != options.Authentication)];
            var catalog = await Catalog(snapshot, order, timeout.Token);
            connection = "passed"; models = catalog.Models;
            discovery = catalog.Status switch { "passed" => "passed", "unsupported" => "unsupported", _ => "failed" };
            if (catalog.Status == "rejected") { authentication = "reconnect"; return Result("failed", UpstreamDetection.Rejected(options)); }
            if (catalog.Status == "cooldown") return Result("cooldown", "上游请求过多；诊断没有改变活动配置的冷却状态。");
            if (catalog.Authentication is not null) authentication = "passed";
            foreach (var method in catalog.Authentication is { } accepted ? UpstreamDetection.StartingFrom(order, accepted) : order)
            {
                var attempt = options with { Authentication = method };
                using var transport = new UpstreamConnection(new(snapshot.Version, attempt, snapshot.Credential), proxy, 1);
                var (reply, diagnostic, triedClaude) = await Call(transport, attempt, timeout.Token);
                used = attempt with { Diagnostic = diagnostic };
                // 401/403 说明这种认证方式不被接受，换下一种；全部用完再判失败。
                if (reply.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) continue;
                authentication = "passed";
                if (reply.Status == HttpStatusCode.TooManyRequests) return Result("cooldown", "最小模型调用受到上游限流，请稍后重新测试。");
                if ((int)reply.Status is < 200 or >= 300) return Result("failed", Refused((int)reply.Status, attempt, diagnostic, triedClaude));
                verifiedModel = ProbeMessage.Read(reply.Body, reply.Streaming, snapshot.Credential!);
                if (verifiedModel is null) return Result("failed", "上游未返回有效的 Anthropic Messages 回复，不能标记调用成功。");
                return Result("passed", UpstreamDetection.Passed(used, verifiedModel)
                    + (diagnostic == "claude-code" ? "Claude Code 能调用不代表通用 API 请求也被这家接受。" : "") + "此检测为管理员诊断，不计入个人业务用量；凭据到期时间未知。");
            }
            authentication = "reconnect"; used = options;
            return Result("failed", UpstreamDetection.Rejected(options));
        }
        // 区分调用方主动取消与超时，两种情况都只返回报告、不激活候选，也不把异常抛给管理页。
        catch (OperationCanceledException) { used = options; return Result(cancellation.IsCancellationRequested ? "cancelled" : "failed", "检测已取消或超时，这个上游没有切换。"); }
        // 建连、地址校验、TLS 或代理失败：标记连接未通过，读出代理原因或固定说明。
        catch (HttpRequestException error) { used = options; connection = "failed"; return Result("failed", ConnectionMessage(error)); }
        // 中转自身拒绝（如重定向、响应超限）：报告里只给固定说明，不回显可能含秘密的原始信息。
        catch (RelayFailure error) { used = options; return Result("failed", error.Status == 502 ? "上游返回跳转、过大或不支持的响应，这个上游没有切换。" : "上游检测失败。"); }
        // 最小调用响应无法解析为 JSON：视为调用未成功，候选不能激活。
        catch (JsonException) { used = options; return Result("failed", "最小调用返回的 JSON 无效，这个上游没有切换。"); }
        // 读取响应时连接中断：结果不完整，按失败处理。
        catch (IOException) { used = options; return Result("failed", "上游响应中断，这个上游没有切换。"); }
        // Claude Code 检测前解析代理失败：读出代理原因，子进程没有启动。
        catch (UpstreamProxyUnavailableException error) { used = options; connection = "failed"; return Result("failed", error.Message); }
        // Claude Code 检测未找到客户端或会话未完成时抛出此异常，转成可操作的提示。
        catch (InvalidOperationException) { used = options; return Result("failed", "Claude Code 检测未完成，请确认本机已安装可用的原生客户端；这个上游没有切换。"); }
        // 系统无法启动 claude.exe 进程（如文件损坏或被拦截）：单独提示，候选不激活。
        catch (System.ComponentModel.Win32Exception) { used = options; return Result("failed", "无法启动本机 Claude Code，这个上游没有切换。"); }
    }

    /// <summary>
    /// 读模型目录：按 <paramref name="order"/> 逐个认证方式请求 <c>/v1/models</c>，第一个被接受的就是结果。照 MyCoAI 的判断：
    /// 400、401、403 且还有下一种方式时换下一种（部分中转缺认证头时回 400）；最后一种也是 401/403、或试过两种以上仍是 400 时判为都被拒。
    /// 404/405 视为不提供目录；其余非 2xx 为 <c>failed</c>（此时 <see cref="ModelCatalog.Message"/> 暂存状态码，由调用方改写成句子）。
    /// </summary>
    private async Task<ModelCatalog> Catalog(UpstreamSnapshot snapshot, string[] order, CancellationToken cancellation)
    {
        for (var index = 0; index < order.Length; index++)
        {
            var method = order[index];
            using var transport = new UpstreamConnection(new(snapshot.Version, snapshot.Options with { Authentication = method }, snapshot.Credential), proxy, 1);
            using var request = transport.Request("/v1/models", "?limit=100", [], "GET", "HouseholdRelay/1.0");
            using var response = await transport.Send(request, cancellation);
            var refused = response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            if (refused && index + 1 < order.Length) continue;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || refused && order.Length > 1) return new("rejected", null, [], "");
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return new("cooldown", null, [], "");
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) return new("unsupported", null, [], "");
            if (!response.IsSuccessStatusCode) return new("failed", null, [], ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            try
            {
                using var body = JsonDocument.Parse(await UpstreamResponses.ReadBounded(stream, 1024 * 1024, cancellation));
                if (body.RootElement.ValueKind != JsonValueKind.Object || !body.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return new("unsupported", method, [], "");
                var models = data.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetProperty("id").GetString()!).Where(model => ProbeMessage.SafeModel(model, snapshot.Credential!)).Distinct().Take(100).ToArray();
                return new("passed", method, models, "");
            }
            // 模型目录不是合法 JSON 只说明上游不支持目录查询；2xx 已说明这种认证方式被接受。
            catch (JsonException) { return new("unsupported", method, [], ""); }
        }
        throw new InvalidOperationException("至少要有一种认证方式。");
    }

    /// <summary>按检测方式做一次最小调用：手动 Claude Code 直接跑实际客户端；手动标准只发标准调用；自动时标准调用非 2xx（401、429 除外）且本机装有 Claude Code 再跑一次实际客户端。返回最后一次回复、采用的检测方式和是否跑过 Claude Code。</summary>
    private async Task<(ProbeReply Reply, string Diagnostic, bool TriedClaude)> Call(UpstreamConnection transport, UpstreamOptions attempt, CancellationToken cancellation)
    {
        if (attempt.DiagnosticManual && attempt.Diagnostic == "claude-code") return (await claudeCode.Run(transport, cancellation), "claude-code", true);
        var standard = await StandardCall(transport, cancellation);
        if (attempt.DiagnosticManual || Accepted(standard, transport.Snapshot.Credential!) || standard.Status is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests)
            return (standard, "messages", false);
        if (!claudeCode.IsAvailable) return (standard, "messages", false);
        return (await claudeCode.Run(transport, cancellation), "claude-code", true);
    }

    /// <summary>标准调用是否已经成功：2xx 且正文是真实的 Messages 回复；正文无法解析时视为未成功。</summary>
    private static bool Accepted(ProbeReply reply, string secret)
    {
        if ((int)reply.Status is < 200 or >= 300) return false;
        try { return ProbeMessage.Read(reply.Body, reply.Streaming, secret) is not null; }
        // 2xx 却不是合法 JSON：按未成功处理，自动检测会再试实际客户端。
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// 调用被拒（非 2xx、非 401/429）时读出的原因与下一步，区分手动标准、手动 Claude Code、自动但本机没装 Claude Code、两种都试过；
    /// 5xx 不是模型或权限问题，一律读成上游暂时不可用或拒绝了当前网络出口，并读出当前代理方式。
    /// </summary>
    private string Refused(int status, UpstreamOptions attempt, string diagnostic, bool triedClaude) =>
        status >= 500 ? (triedClaude ? $"标准 Messages 和 Claude Code 实际调用都没有通过（最后一次 HTTP {status}）。" : $"最小模型调用返回 HTTP {status}。") + Unavailable()
        : attempt.DiagnosticManual && diagnostic == "messages" ? $"最小模型调用返回 HTTP {status}，请检查模型权限和上游配置。若这家只接受 Claude Code，可在高级设置把检测方式改回自动判断。"
        : attempt.DiagnosticManual ? $"Claude Code 实际调用返回 HTTP {status}，请检查模型名称和权限。"
        : triedClaude ? $"标准 Messages 和 Claude Code 实际调用都没有通过（最后一次 HTTP {status}），请检查模型名称和权限。"
        : claudeCode.IsAvailable ? $"最小模型调用返回 HTTP {status}，请检查模型名称和权限。"
        : $"标准 Messages 调用被上游拒绝（HTTP {status}），本机没有安装 Claude Code，无法再用实际客户端测试。请检查模型权限，或在本机安装 Claude Code 后重试。";

    /// <summary>标准 Messages 检测：用候选配置的模型发一次非流式、最多 16 个令牌的最小调用，读回有界正文（上限 1 MB）交给调用方校验；请求与响应在方法内释放。</summary>
    private static async Task<ProbeReply> StandardCall(UpstreamConnection transport, CancellationToken cancellation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { model = transport.Snapshot.Options.Model, max_tokens = 16, stream = false,
            messages = new[] { new { role = "user", content = "Reply with OK." } } });
        using var call = transport.Request("/v1/messages", "", bytes, "POST", "HouseholdRelay/1.0");
        call.Content!.Headers.ContentType = new("application/json");
        using var result = await transport.Send(call, cancellation);
        await using var content = await result.Content.ReadAsStreamAsync(cancellation);
        return new(result.StatusCode, await UpstreamResponses.ReadBounded(content, 1024 * 1024, cancellation), result.Content.Headers.ContentType?.MediaType == "text/event-stream");
    }

    /// <summary>建连失败的读句：代理取不到时读出代理原因，其余给固定说明，不回显底层网络细节。</summary>
    private static string ConnectionMessage(HttpRequestException error)
    {
        for (Exception? inner = error; inner is not null; inner = inner.InnerException)
            if (inner is UpstreamProxyUnavailableException proxyError) return proxyError.Message;
        return "连接或 TLS 校验失败，请检查地址、端口、证书及项目设置里的代理。";
    }

    /// <summary>上游回 5xx 时的原因与下一步：可能是上游暂时故障，也可能是按地区屏蔽了当前网络出口；附上当前代理方式，指向「查代理出口」而不是「查地址」。</summary>
    private string Unavailable() => $"上游暂时不可用，或者拒绝了当前网络出口（例如按地区屏蔽）。当前代理设置：{proxy.Describe()}请检查代理出口后重试。";

    /// <summary>获取模型失败时的结果：认证方式未确定，目录为空。</summary>
    private static ModelCatalog Failed(string message) => new("failed", null, [], message);

    /// <summary>链接调用方的取消并加上 <paramref name="seconds"/> 秒上限。</summary>
    private static CancellationTokenSource Limit(CancellationToken cancellation, int seconds)
    {
        // 管理员关掉页面（调用方取消）或上游一直不应答（到时限）都要及时停下网络请求和子进程；返回的令牌源由调用方释放。
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellation); source.CancelAfter(TimeSpan.FromSeconds(seconds)); return source;
    }
}
