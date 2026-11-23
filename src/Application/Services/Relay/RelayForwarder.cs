using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using Application.Interfaces.ClaudeCode;
using Application.Interfaces.Configuration;
using Application.Interfaces.Relay;
using Application.Interfaces.Upstream;
using Application.Interfaces.Usage;
using Application.Logging;
using Core.Configuration;
using Core.Identity;
using Core.Relay;
using Core.Upstream;
using Core.Usage;
using Core.Values;
using Microsoft.Extensions.Logging;

namespace Application.Services.Relay;

/// <summary>中转的转发编排：鉴权、准入、用量记录、请求改写、上游转发与自动切换、错误分类；宿主只把请求包成 <see cref="IRelayExchange"/> 交进来。</summary>
public sealed class RelayForwarder : IRelayForwarder
{
    private readonly RelayConfiguration _config;
    private readonly ClientRegistry _clients;
    private readonly Admission _admission;
    private readonly IUpstreamPool _upstreams;
    private readonly IRelayEventLog _log;
    private readonly IUpstreamResponses _responses;
    private readonly ILogger<RelayForwarder> _logger;
    // 下面六个只在正式服务里有；独立中转（Standalone）明确没有它们：不记用量、不发往官方账户、不跟客户端版本、不进站点维护、不取上游概览、不记协议诊断。
    private readonly IUsageService? _usage;
    private readonly IClaudeCodeIdentitySource? _identities;
    private readonly IClaudeCodeUpdater? _updater;
    private readonly ISiteIdentityService? _siteIdentity;
    private readonly IUpstreamService? _upstreamService;
    private readonly IProtocolDiagnostics? _diagnostics;
    private long _completed;

    /// <summary>正式服务：十三个依赖全部必填，构造时当场判空，漏登记在启动时就报，不会静默退成「不记录用量」。</summary>
    public RelayForwarder(RelayConfiguration config, ClientRegistry clients, Admission admission, IUpstreamPool upstreams, IRelayEventLog log, IUpstreamResponses responses, ILogger<RelayForwarder> logger,
        IUsageService usage, IClaudeCodeIdentitySource identities, IClaudeCodeUpdater updater, ISiteIdentityService siteIdentity, IUpstreamService upstreamService, IProtocolDiagnostics diagnostics)
        : this(config, clients, admission, upstreams, log, responses, logger)
    {
        ArgumentNullException.ThrowIfNull(usage); ArgumentNullException.ThrowIfNull(identities); ArgumentNullException.ThrowIfNull(updater);
        ArgumentNullException.ThrowIfNull(siteIdentity); ArgumentNullException.ThrowIfNull(upstreamService); ArgumentNullException.ThrowIfNull(diagnostics);
        _usage = usage; _identities = identities; _updater = updater; _siteIdentity = siteIdentity; _upstreamService = upstreamService; _diagnostics = diagnostics;
    }

    /// <summary>
    /// 独立中转（不连数据库）：只要转发本身的六样。不记用量，个人用量入口返回 503；发往官方账户的请求一律不发并读出原因；不跟客户端版本；
    /// 设备值用启动配置里的那一个；/status 直接问连接池。
    /// </summary>
    public static RelayForwarder Standalone(RelayConfiguration config, ClientRegistry clients, Admission admission, IUpstreamPool upstreams, IRelayEventLog log, IUpstreamResponses responses,
        ILogger<RelayForwarder> logger) => new(config, clients, admission, upstreams, log, responses, logger);

    /// <summary>两个入口共用的那一半：转发本身的七样依赖（含兜底用的日志），当场判空并校验配置。</summary>
    private RelayForwarder(RelayConfiguration config, ClientRegistry clients, Admission admission, IUpstreamPool upstreams, IRelayEventLog log, IUpstreamResponses responses, ILogger<RelayForwarder> logger)
    {
        ArgumentNullException.ThrowIfNull(config); ArgumentNullException.ThrowIfNull(clients); ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(upstreams); ArgumentNullException.ThrowIfNull(log); ArgumentNullException.ThrowIfNull(responses); ArgumentNullException.ThrowIfNull(logger);
        config.Validate();
        _config = config; _clients = clients; _admission = admission; _upstreams = upstreams; _log = log; _responses = responses; _logger = logger;
    }

    /// <summary>
    /// 取发往官方账户的统一身份：按客户端自报的入口取同一种模式的那一份，请求头与正文的入口一致；没有登记身份来源时按「没取到」抛出，请求不发。
    /// 取到后把客户端版本与这份身份的版本交给升级器，客户端更新时中转在后台升级，这次请求照常用当前身份发。
    /// </summary>
    private async Task<ClaudeCodeIdentity> IdentityAsync(ClaudeCodeClient client, long? clientId, CancellationToken cancellation)
    {
        var identity = await (_identities?.CurrentAsync(client.IdentityEntrypoint, cancellation) ?? throw new ClaudeCodeIdentityUnavailableException(ClaudeCodeIdentity.Missing));
        _updater?.Observe(client.Version, identity.Version, clientId);
        return identity;
    }

    /// <summary>中转请求处理入口，对外宿主直接使用，管理宿主也把未匹配页面的请求兜底转到这里：/healthz 无需令牌；个人用量入口先行处理；其余请求须带有效令牌，只开放 /status、/api/hello 与三个 /v1 路由。流程为记录用量开始、限速与排队准入、限长读取请求体、校验 JSON 并按需改写设备元数据、记录发送、转发并流式回传，期间多次重新核验令牌。无论成败都在 finally 中写用量结束结果和一行不含令牌的事件日志。</summary>
    public async Task HandleAsync(IRelayExchange exchange)
    {
        var stopwatch = Stopwatch.StartNew(); var id = Guid.NewGuid().ToString("N"); var name = "unknown";
        var path = exchange.Path; var outcome = UsageOutcome.Succeeded;
        UsageAccumulator? usage = null; var recorded = false; var dispatched = false; int? upstreamStatus = null;
        var attempts = new List<UpstreamAttempt>(); string? upstreamName = null;
        var heldOfficialOperations = new List<IDisposable>();
        try
        {
            if (path == "/healthz" && exchange.Method == "GET")
            { await exchange.Reply.WriteJsonAsync(new { ok = true }, exchange.Aborted); return; }
            if (await PersonalUsage.TryHandleAsync(exchange, _clients, _usage)) return;
            var token = RelayToken.From(exchange);
            var client = await _clients.AuthenticateAsync(token) ?? throw new RelayFailure(401, "Invalid or disabled relay token.");
            name = client.Name;
            if (path == "/status" && exchange.Method == "GET")
            {
                exchange.Reply.SetHeader("Cache-Control", ["no-store"]);
                var overview = _upstreamService is null ? null : await _upstreamService.ReadOverviewAsync();
                using var status = overview is null ? await _upstreams.AcquireAsync() : null;
                var paused = overview is null ? status!.Paused : _upstreams.Status(overview.Version).Paused;
                await exchange.Reply.WriteJsonAsync(new { client = name, active = _admission.Active, queued = _admission.Queued,
                    upstreamPaused = paused, completed = Interlocked.Read(ref _completed) }, exchange.Aborted); return;
            }
            if (path == "/api/hello" && exchange.Method == "HEAD") { exchange.Reply.StatusCode = 200; return; }
            if (!RelayRoutes.Exposed.TryGetValue(path, out var method)) throw new RelayFailure(404, "Endpoint not exposed by this relay.");
            if (exchange.Method != method) throw new RelayFailure(405, "Method not allowed.");
            var kind = RelayRoutes.Kind(path);
            usage = new UsageAccumulator(kind);
            if (_usage is not null) { await _usage.StartAsync(id, client, kind); recorded = true; }
            using var route = await _upstreams.RouteAsync();
            await route.CheckStateAsync(); _admission.CheckRate(client.Id);
            using var lease = await _admission.EnterAsync(client.Id, exchange.Aborted);
            if (await _clients.AuthenticateAsync(token) is null) throw new RelayFailure(401, "Relay token revoked while queued.");
            await route.CheckStateAsync();
            var body = await ReadBodyAsync(exchange);
            if (await _clients.AuthenticateAsync(token) is null) throw new RelayFailure(401, "Relay token revoked during upload.");
            await route.CheckStateAsync();
            string? model = null; byte[]? normalized = null;
            // 兼容账户的设备值取当前已发布的站点快照：管理员换号后不重启也发新编号，不用启动时那份配置里的旧值。
            var compatibleDevice = _siteIdentity?.Current.DeviceId ?? _config.DeviceId;
            if (method == "POST")
            {
                if (!(exchange.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                    throw new RelayFailure(415, "Expected application/json.");
                try
                {
                    using var document = JsonDocument.Parse(body);
                    if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                    if (document.RootElement.TryGetProperty("model", out var modelValue) && modelValue.ValueKind == JsonValueKind.String)
                        model = SafeModel(modelValue.GetString(), token, route.Active.Snapshot.Credential);
                    if (!route.Active.Snapshot.Options.OfficialAccount && route.Active.Snapshot.Options.Mode == UpstreamMode.OAuth && _config.NormalizeDeviceMetadata) normalized = DeviceMetadata.Normalize(body, compatibleDevice);
                }
                // 请求体不是 JSON 对象时在发往上游之前就以 400 拒绝，不把解析器的内部消息透给客户端。
                catch (JsonException) { throw new RelayFailure(400, "Expected a JSON object."); }
            }
            // JSON 处理完成后再核验，缩短上传结束与真正发往上游之间的撤销窗口。
            if (await _clients.AuthenticateAsync(token) is null) throw new RelayFailure(401, "Relay token revoked before dispatch.");
            // 每家按自己的接入方式用同一份请求体重建请求：账户模式统一设备元数据，API 模式原样；请求体只读了一次。
            byte[] Body(IUpstreamLease target) => method == "POST" && !target.Snapshot.Options.OfficialAccount && target.Snapshot.Options.Mode == UpstreamMode.OAuth && _config.NormalizeDeviceMetadata
                ? normalized ??= DeviceMetadata.Normalize(body, compatibleDevice) : body;
            // 旧接法的官方账户请求关闭后，选上游时跳过一切官方账户（含备用的，不能借备用官方绕过）；独立 API 与兼容中转照旧，一家都不剩就本地拒绝。
            async Task<IUpstreamLease?> NextAsync()
            {
                var next = await route.NextAsync(model);
                while (_config.LegacyOfficialClosed && next?.Snapshot.Options.OfficialAccount == true) next = await route.NextAsync(model);
                return next;
            }
            var upstream = await NextAsync() ?? throw new RelayFailure(403, LegacyOfficialClosedSentence);
            // 发往官方账户要和原生直连一致，家里的 Claude Code 得开「按官方直连发请求」开关（它开了每个对话请求都带 X-Client-Request-Id）。
            // 没开就少一批只有客户端自己能生成的值，这次不发往官方，把开开关的命令读给使用者，复制执行、重开 Claude Code 再发即可。
            if (path == "/v1/messages" && upstream.Snapshot.Options.OfficialAccount
                && FirstPartySwitch.Missing(exchange.Header("User-Agent").ToString(), exchange.Header("X-Client-Request-Id").ToString()))
                throw new RelayFailure(400, FirstPartySwitch.Instruction);
            // 客户端自报的 Claude Code 版本与入口，第一次发往官方账户时才读一次。
            ClaudeCodeClient? caller = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(exchange.Aborted);
            // 总超时与客户端断开共用同一个令牌：客户端断开时立即取消上游调用，超时时也会取消，二者在下方 catch 里按 Aborted 区分。
            // 换家时不重新计时，几家加起来也不超过同一个总超时。
            timeout.CancelAfter(TimeSpan.FromSeconds(_config.RequestTimeoutSeconds));
            UpstreamAttempt? attempt = null; var classified = false; Func<string, string> redact = value => value;
            try
            {
                try
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    while (true)
                    {
                        attempt = new UpstreamAttempt(attempts.Count + 1, upstream.Snapshot.UpstreamCandidateId, upstream.Snapshot.ActiveVersion, _usage?.Now ?? DateTimeOffset.UtcNow);
                        attempts.Add(attempt); classified = false; upstreamName = upstream.Snapshot.Name; upstreamStatus = null;
                        // 尝试记录里的说明先去掉这一家的上游凭据和个人令牌，再截断落库；每家只用自己的凭据，不会带到别家。
                        var secret = upstream.Snapshot.Credential!;
                        redact = value => _responses.Redact(_responses.Redact(value, secret), token!);
                        SiteIdentitySnapshot? attemptSite = null;
                        var priorDispatched = dispatched;
                        var attemptSent = false;
                        HttpRequestMessage? request = null; HttpResponseMessage response;
                        try
                        {
                            // 准入也处在可换家的区域；维护恰在选中官方后关闭时，仍允许独立 API 备用继续。
                            var operation = upstream.Snapshot.Options.OfficialAccount ? _siteIdentity?.Enter() : null;
                            if (operation is not null) heldOfficialOperations.Add(operation);
                            attemptSite = _siteIdentity?.Current;
                            // 官方账户只带中转所在电脑 Claude Code 的统一身份；取不到就不组装、不发出，下面按可切换的失败处理。
                            var identity = upstream.Snapshot.Options.OfficialAccount ? await IdentityAsync(caller ??= ClaudeCodeClient.Read(body, exchange.Header("User-Agent")), client.UserId, timeout.Token) : null;
                            request = upstream.Request(path, exchange.QueryString, Body(upstream), method, _config.UpstreamUserAgent, exchange.Headers, identity, attemptSite?.DeviceId ?? _config.DeviceId);
                            if (upstream.Snapshot.Options.OfficialAccount)
                            {
                                _diagnostics?.Record(id, "assembled", path, attemptSite?.Version ?? 0, attemptSite?.DeviceId ?? _config.DeviceId, identity?.Version, caller?.IdentityEntrypoint);
                            }
                            if (!dispatched && recorded) await _usage!.DispatchAsync(id, upstream.Snapshot.ActiveVersion, upstream.Snapshot.UpstreamCandidateId, model);
                            dispatched = true; attemptSent = true;
                            if (upstream.Snapshot.Options.OfficialAccount)
                                _diagnostics?.Record(id, "dispatch", path, attemptSite?.Version ?? 0, attemptSite?.DeviceId ?? _config.DeviceId, identity?.Version, caller?.IdentityEntrypoint);
                            response = await upstream.SendAsync(request, timeout.Token);
                        }
                        // 先按原始异常给这次尝试分类；连不上（含建连超时、TLS 失败）或取不到统一身份，且顺序里还有下一家时换下一家（连不上的那家暂避），否则原样抛给下面的转换，返回给客户端的内容不变。
                        catch (Exception error)
                        {
                            request?.Dispose();
                            // 解析代理已明确失败时本次没有建连；保留前面候选真正发送过的标记，不按尝试次数猜测。
                            if (error is UpstreamProxyUnavailableException || error is HttpRequestException && ProxyUnavailable(error)) dispatched = priorDispatched;
                            var identityBlocked = error is SiteIdentityUnavailableException or ClaudeConfigurationUnavailableException;
                            attemptSite ??= _siteIdentity?.Current;
                            if (upstream.Snapshot.Options.OfficialAccount && !attemptSent && (error is RelayFailure or ClaudeCodeIdentityUnavailableException || identityBlocked))
                                _diagnostics?.Record(id, "local_rejection", path, attemptSite?.Version ?? 0, attemptSite?.DeviceId ?? _config.DeviceId, error: "local_rejection");
                            attempt.Failed(error, exchange.Aborted.IsCancellationRequested, redact); classified = true;
                            if (!attempt.Switchable && !identityBlocked) throw;
                            IUpstreamLease? after;
                            // 维护/配置拒绝不通过换另一个官方账户绕过；只允许现有策略中的独立 API 候选。
                            do { after = await NextAsync(); } while (identityBlocked && after?.Snapshot.Options.OfficialAccount == true);
                            if (after is null) throw;
                            attempt.PassOn();
                            if (!identityBlocked) route.Avoid(upstream, attempt);
                            upstream = after; continue;
                        }
                        // 官方许可还要覆盖下方最终用量落账，统一在 Handle 的最外层 finally 归还。
                        using (request)
                        using (response)
                        {
                            upstreamStatus = (int)response.StatusCode;
                            attempt.Headers(upstreamStatus.Value, UpstreamRequestId(response), redact);
                            upstream.Observe(response);
                            // 还没向客户端写出任何字节：5xx、429、401、403 且顺序里还有下一家时，只读出这家的错误说明记进尝试，丢弃响应，换下一家。
                            if (attempt.Switchable && await NextAsync() is { } next)
                            {
                                var discarded = new UsageAccumulator(kind);
                                await _responses.InspectAsync(response, secret, discarded, timeout.Token);
                                attempt.Completed(upstreamStatus.Value, discarded, redact); attempt.PassOn(); route.Avoid(upstream, attempt);
                                if (upstream.Snapshot.Options.OfficialAccount)
                                    _diagnostics?.Record(id, "response", path, attemptSite?.Version ?? 0, attemptSite?.DeviceId ?? _config.DeviceId,
                                        entrypoint: caller?.IdentityEntrypoint, status: upstreamStatus, error: DiagnosticError(upstreamStatus.Value, discarded));
                                upstream = next; continue;
                            }
                            // 从这里开始向客户端写状态码和正文，之后出什么错都不再换家。
                            await _responses.CopyAsync(response, exchange.Reply, secret, id, usage, timeout.Token);
                            attempt.Completed(upstreamStatus.Value, usage, redact);
                            if (upstream.Snapshot.Options.OfficialAccount)
                                _diagnostics?.Record(id, "response", path, attemptSite?.Version ?? 0, attemptSite?.DeviceId ?? _config.DeviceId,
                                    entrypoint: caller?.IdentityEntrypoint, status: upstreamStatus, error: DiagnosticError(upstreamStatus.Value, usage));
                            if (!response.IsSuccessStatusCode || usage.Error) outcome = UsageOutcome.Failed;
                            else if (usage.IsStream && !usage.ProtocolComplete) outcome = UsageOutcome.Interrupted;
                            else route.Served(upstream);
                            Interlocked.Increment(ref _completed);
                        }
                        break;
                    }
                }
                // 回传途中等尚未分类的异常：先给这次尝试分类，再原样抛给下面和外层的转换；分类只进记录，返回给客户端的内容不变。
                catch (Exception error) when (attempt is not null && !classified) { attempt.Failed(error, exchange.Aborted.IsCancellationRequested, redact); throw; }
            }
            // 客户端仍在连接却被取消，只能是总超时触发，转成 504；客户端自己断开的情况留给外层按 cancelled 处理。
            catch (OperationCanceledException) when (!exchange.Aborted.IsCancellationRequested)
            { throw new RelayFailure(504, "Upstream request timed out."); }
            // 代理已启用却取不到地址：建连前拒绝；发送标记只表示已交给发送器，不冒充上游已接收。
            // 之前已经换过家时前面那几次确实发出过，仍记为已发送。固定提示让使用者知道去找管理员查代理，不透出代理地址。
            // 给官方账户取统一身份时本机 Claude Code 也按全局代理起，同样在发出之前就拒绝。
            catch (Exception error) when (error is UpstreamProxyUnavailableException || error is HttpRequestException && ProxyUnavailable(error))
            { throw new RelayFailure(502, "Upstream proxy is enabled but unavailable; the request was not sent. Ask the administrator to check proxy settings in local management."); }
            // 连不上上游时只回固定的 502 提示，不透出上游地址或底层网络错误细节。
            catch (HttpRequestException) { throw new RelayFailure(502, "Upstream connection failed. Check host connectivity."); }
            // 官方账户取不到中转所在电脑 Claude Code 的统一身份：这一家没有发出，回 503 并读出原因与下一步（与用量页、上游页同一句）；不是官方的问题，不进认证暂停也不进冷却。
            // 之前已经换过家时前面那几次确实发出过，仍记为已发送。
            catch (ClaudeCodeIdentityUnavailableException error) { throw new RelayFailure(503, error.Message); }
        }
        // 站点维护属于本地拒绝，不调用上游 Observe，也不触发认证暂停或冷却。
        catch (Exception error) when (error is SiteIdentityUnavailableException or ClaudeConfigurationUnavailableException)
        { outcome = UsageOutcome.Failed; await exchange.FailAsync(new RelayFailure(503, error.Message), id); }
        // 客户端主动断开：记为 cancelled 并直接中止连接，不再尝试写错误响应。
        catch (OperationCanceledException) when (exchange.Aborted.IsCancellationRequested) { outcome = UsageOutcome.Cancelled; exchange.Reply.Abort(); }
        // 鉴权、限速、准入、校验等可预期的拒绝，按其状态码和固定消息写回；宿主读请求时发现格式错误或超限，也已在读请求体处换成这一种。
        catch (RelayFailure error) { outcome = UsageOutcome.Failed; await exchange.FailAsync(error, id); }
        // 回传上游响应途中流被打断，记为 interrupted；若已开始写响应，Fail 会直接中止连接。
        catch (IOException) { outcome = UsageOutcome.Interrupted; await exchange.FailAsync(new RelayFailure(502, "Upstream response interrupted."), id); }
        catch (Exception error)
        {
            // 兜底：未预料的异常只回 500 固定消息（带请求编号），不泄露堆栈、令牌或上游凭据；日志只记异常类型链、阶段、请求编号与这次在试的上游主键。
            outcome = UsageOutcome.Failed;
            RelayLog.Unexpected(_logger, error, "relay", id, attempts.LastOrDefault()?.UpstreamCandidateId);
            await exchange.FailAsync(new RelayFailure(500, "Internal relay error."), id);
        }
        // 无论成败都要写用量结束结果和事件日志，保证已开始的用量记录不会悬空；日志只含请求编号、客户端名和路由，不含令牌。
        // 事件日志在原有字段之后补上游名称、尝试次数和最后一次错误类型，原有字段与顺序不变。
        finally
        {
            try
            {
                if (recorded)
                    await _usage!.FinishAsync(id, new UsageEnd(outcome, dispatched, exchange.Reply.StatusCode, upstreamStatus,
                        stopwatch.ElapsedMilliseconds, usage!.Completeness(dispatched, outcome == UsageOutcome.Succeeded), usage.Tokens,
                        usage.ReportedMoney is { } money ? money with { Completeness = usage.ProtocolComplete && outcome == UsageOutcome.Succeeded ? MoneyCompleteness.Complete : MoneyCompleteness.Partial } : null)
                        { Attempts = attempts });
                _log.Write(new { time = DateTimeOffset.UtcNow, requestId = id, client = name, route = RelayRoutes.IsExposed(path) ? path : "local",
                    status = exchange.Reply.StatusCode, outcome = Wire<UsageOutcome>.Name(outcome), durationMs = stopwatch.ElapsedMilliseconds,
                    upstream = upstreamName, attempts = attempts.Count, error = attempts.LastOrDefault()?.ErrorType });
            }
            // SSE 完整结束且尾部用量写入完成后才让维护提交；异常和换家也在此释放，避免提前排空或泄漏许可。
            finally { for (var index = heldOfficialOperations.Count - 1; index >= 0; index--) heldOfficialOperations[index].Dispose(); }
        }
    }

    /// <summary>旧接法官方账户请求关闭后、又没有别的上游可用时读给使用者的话。</summary>
    internal const string LegacyOfficialClosedSentence = "这台中转已经不再接受旧接法发来的官方账户请求。请在 MyCoAI 里改用思域启动 Claude Code。";

    /// <summary>只从既有解析结果分类错误，不额外读取或复制正文，也不把错误原话放进诊断样本。</summary>
    private static string? DiagnosticError(int status, UsageAccumulator usage)
    {
        var type = usage.ErrorType ?? ""; var message = usage.ErrorMessage ?? "";
        if (message.Contains("verification", StringComparison.OrdinalIgnoreCase) || message.Contains("verify", StringComparison.OrdinalIgnoreCase)) return "verification";
        if (message.Contains("model", StringComparison.OrdinalIgnoreCase) && status is 400 or 403 or 404) return "model_limit";
        return status switch { 401 => "authentication", 403 => "forbidden", 429 => "rate_limit", _ => type == "rate_limit_error" ? "rate_limit" : null };
    }
    /// <summary>取上游响应头里的请求编号：先看 Anthropic 的 request-id，再看通用的 x-request-id；都没有时为 null。</summary>
    private static string? UpstreamRequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("request-id", out var values) || response.Headers.TryGetValues("x-request-id", out values) ? values.FirstOrDefault() : null;

    /// <summary>建连异常链里是否有「代理已启用却取不到地址」；有就说明请求根本没有发出。</summary>
    private static bool ProxyUnavailable(Exception error) => ExceptionChain.Find<UpstreamProxyUnavailableException>(error) is not null;

    /// <summary>把请求体里的模型名过滤成可安全写入用量记录的值：不合乎 <see cref="ModelName"/>，或包含个人令牌、上游凭据时一律记为空，防止秘密借模型字段落库。</summary>
    private static string? SafeModel(string? model, string? token, string? credential)
    {
        if (!ModelName.IsValid(model)) return null;
        if (token is { Length: > 0 } && model.Contains(token, StringComparison.Ordinal)
            || credential is { Length: > 0 } && model.Contains(credential, StringComparison.Ordinal)) return null;
        return model;
    }

    /// <summary>在 <see cref="RelayConfiguration.UploadTimeoutSeconds"/> 内把请求体读入内存：声明长度或实际累计超过 <see cref="RelayConfiguration.MaxBodyBytes"/> 时返回 413，上传超时返回 408；读缓冲取自共享池，用完清零归还。</summary>
    private async Task<byte[]> ReadBodyAsync(IRelayExchange exchange)
    {
        if (exchange.ContentLength > _config.MaxBodyBytes) throw new RelayFailure(413, "Request body exceeds relay limit.");
        // 与客户端断开联动：断开时立即停止读取，不再占用连接与内存。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(exchange.Aborted);
        // 上传限时，防止慢速上传长期占着已取得的并发名额。
        timeout.CancelAfter(TimeSpan.FromSeconds(_config.UploadTimeoutSeconds));
        using var memory = new MemoryStream(); var buffer = ArrayPool<byte>.Shared.Rent(32768);
        try
        {
            int count;
            while ((count = await exchange.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (memory.Length + count > _config.MaxBodyBytes) throw new RelayFailure(413, "Request body exceeds relay limit.");
                memory.Write(buffer, 0, count);
            }
            return memory.ToArray();
        }
        // 客户端仍在连接却被取消，说明是上传超时，转成 408；客户端断开的取消原样上抛，由外层记为 cancelled。
        catch (OperationCanceledException) when (!exchange.Aborted.IsCancellationRequested) { throw new RelayFailure(408, "Request upload timed out."); }
        // 缓冲区里可能残留请求内容（含提示词等），归还共享池前先清零，避免被其他请求读到。
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }
}
