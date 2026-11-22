using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.Contracts.Siyu;
using Application.Interfaces.Siyu;
using Core.Siyu;

namespace HouseholdRelay.Siyu;

/// <summary>
/// 思域控制面的 HTTP 绑定，挂在站点对外 HTTPS（与中转同一端口）的 <c>/v1/siyu</c> 前缀下：
/// <c>GET /accounts</c> 列可选官方账户（200，<c>{"accounts":[{"officialAccountId","displayName"}]}</c>）；
/// <c>POST /sessions</c> 建会话（201）、<c>GET /sessions/{id}</c> 查询（200）、<c>PUT /sessions/{id}/lease</c> 续租（200）、<c>DELETE /sessions/{id}</c> 停止（Closed 回 204，Closing 回 202 带视图）。
/// 只做 HTTP 与 JSON 的翻译，规则全在 <see cref="ISiyuSessionService"/>；个人令牌只认 <c>Authorization: Bearer</c>，不进 URL、日志或回应；带请求体的先鉴权再读体。所有回应 <c>Cache-Control: no-store</c>。
/// </summary>
public sealed class SiyuControl
{
    /// <summary>挂载前缀。</summary>
    public const string Prefix = "/v1/siyu";

    /// <summary>请求体上限：建会话的字段都很短，8 KiB 足够。</summary>
    internal const int MaxBodyBytes = 8 * 1024;

    /// <summary>请求 JSON：驼峰名、不认识的字段拒绝、必填构造参数缺了拒绝、可空注解当真。</summary>
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict,
    };

    private readonly ISiyuSessionService _sessions;

    /// <summary>会话服务必填。</summary>
    public SiyuControl(ISiyuSessionService sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
    }

    /// <summary>处理前缀下的一个请求（<see cref="HttpRequest.Path"/> 已去掉前缀）：按方法与剩余路径分派，拒绝一律回固定 JSON 错误体。</summary>
    public async Task Handle(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var token = BearerToken(context.Request);
            var segments = (context.Request.Path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
            var method = context.Request.Method;
            switch (segments)
            {
                case ["accounts"] when HttpMethods.IsGet(method):
                    var accounts = await _sessions.ListAccounts(token);
                    await Write(context, StatusCodes.Status200OK, writer => WriteAccounts(writer, accounts)); return;
                case ["sessions"] when HttpMethods.IsPost(method):
                    // 先鉴权再读请求体：没带或带错令牌的请求不读体，直接 401。
                    await _sessions.Authorize(token);
                    var request = await ReadJson<SiyuCreateRequest>(context);
                    var created = await _sessions.Create(token, request, context.RequestAborted);
                    await Write(context, StatusCodes.Status201Created, writer => WriteCreated(writer, created)); return;
                case ["sessions", var id] when HttpMethods.IsGet(method):
                    var read = await _sessions.Read(token, id);
                    await Write(context, StatusCodes.Status200OK, writer => WriteView(writer, read)); return;
                case ["sessions", var id, "lease"] when HttpMethods.IsPut(method):
                    await _sessions.Authorize(token);
                    var lease = await ReadJson<SiyuLeaseRequest>(context);
                    var renewed = await _sessions.Renew(token, id, lease.LeaseSequence);
                    await Write(context, StatusCodes.Status200OK, writer => WriteView(writer, renewed)); return;
                case ["sessions", var id] when HttpMethods.IsDelete(method):
                    var closed = await _sessions.Close(token, id);
                    if (closed.State == SiyuSessionState.Closed) { context.Response.StatusCode = StatusCodes.Status204NoContent; return; }
                    await Write(context, StatusCodes.Status202Accepted, writer => WriteView(writer, closed)); return;
                default:
                    throw new SiyuRefusal(404, "not-found", "思域控制面没有这个地址。请升级 MyCoAI 后再试。");
            }
        }
        // 业务拒绝：状态码、原因码与读句都来自会话服务；身份代次过时另带当前代次，供 MyCoAI 重新准备。
        catch (SiyuRefusal refusal) { await WriteRefusal(context, refusal); }
        // 客户端断开：回应已经没人收，不再写。
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    }

    /// <summary>思域没开（项目设置里的上线开关关着）时前缀下的每个请求都回 503，读出怎么打开；不碰会话服务。</summary>
    public static Task Disabled(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.CacheControl = "no-store";
        return WriteRefusal(context, new SiyuRefusal(503, "siyu-disabled", "这台中转还没有打开思域。请管理员在中转的项目设置里勾选「接受新的思域会话」，然后重启中转服务。"));
    }

    /// <summary>取唯一的 <c>Authorization: Bearer</c> 值；没有、多个或不是 Bearer 时为 null（会话服务回 401）。</summary>
    private static string? BearerToken(HttpRequest request)
    {
        var values = request.Headers.Authorization;
        if (values.Count != 1 || values[0] is not { } value || !value.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        var token = value["Bearer ".Length..];
        return token.Length == 0 || token.Contains(' ', StringComparison.Ordinal) ? null : token;
    }

    /// <summary>按严格 JSON 合同读请求体：必须是 application/json、有界、字段一个不多一个不少。</summary>
    private static async Task<T> ReadJson<T>(HttpContext context) where T : class
    {
        var request = context.Request;
        if (request.ContentType is not { } type || !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            throw new SiyuRefusal(415, "content-type", "思域控制面只收 JSON。请升级 MyCoAI 后再试。");
        if (request.ContentLength is > MaxBodyBytes) throw TooLarge();
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) throw TooLarge();
            buffer.Write(chunk, 0, read);
        }
        try { return JsonSerializer.Deserialize<T>(buffer.ToArray(), _json) ?? throw Malformed(); }
        // 字段缺失、多出、类型不符都在这里：不回显原文，只给固定读句。
        catch (JsonException) { throw Malformed(); }
    }

    /// <summary>请求体超限。</summary>
    private static SiyuRefusal TooLarge() => new(413, "body-too-large", "思域控制面的请求太大。请升级 MyCoAI 后再试。");

    /// <summary>请求体格式不合合同。</summary>
    private static SiyuRefusal Malformed() => new(400, "request-malformed", "思域控制面看不懂这次请求的内容。请升级 MyCoAI 后再试。");

    /// <summary>写一个 JSON 回应。</summary>
    private static async Task Write(HttpContext context, int status, Action<Utf8JsonWriter> body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        var buffer = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(buffer)) body(writer);
        await context.Response.Body.WriteAsync(buffer.ToArray(), context.RequestAborted);
    }

    /// <summary>拒绝的错误体，与代理入口本地拒绝同一形状；401 另带 <c>WWW-Authenticate: Bearer</c>。</summary>
    private static Task WriteRefusal(HttpContext context, SiyuRefusal refusal)
    {
        if (refusal.Status == StatusCodes.Status401Unauthorized) context.Response.Headers.WWWAuthenticate = "Bearer realm=\"siyu\"";
        return Write(context, refusal.Status, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "error");
            writer.WriteStartObject("error");
            writer.WriteString("type", "siyu_refused");
            writer.WriteString("code", refusal.Code);
            writer.WriteString("message", refusal.Sentence);
            if (refusal.CurrentIdentityVersion is { } current) writer.WriteNumber("currentIdentityVersion", current);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    /// <summary>账户列表：只有主键与显示名称；一个都没有是空数组。</summary>
    private static void WriteAccounts(Utf8JsonWriter writer, IReadOnlyList<SiyuAccountOption> accounts)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("accounts");
        foreach (var account in accounts)
        {
            writer.WriteStartObject();
            writer.WriteNumber("officialAccountId", account.OfficialAccountId);
            writer.WriteString("displayName", account.DisplayName);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>建会话的结果：会话视图、启动身份（oauthAccount 与凭据资料按原字节嵌入）、代理接入。</summary>
    private static void WriteCreated(Utf8JsonWriter writer, SiyuSessionCreated created)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("session"); WriteView(writer, created.Session);
        writer.WriteStartObject("launchIdentity");
        writer.WriteString("deviceId", created.LaunchIdentity.DeviceId);
        // 原字节嵌入，不经反序列化再写：客户端写进 .claude.json 的 oauthAccount 与中转机原生写的逐字相同。
        writer.WritePropertyName("oauthAccount"); writer.WriteRawValue(created.LaunchIdentity.OAuthAccountJson.Span, skipInputValidation: false);
        writer.WritePropertyName("credentialMetadata"); writer.WriteRawValue(created.LaunchIdentity.CredentialMetadataJson.Span, skipInputValidation: false);
        writer.WriteEndObject();
        writer.WriteStartObject("proxy");
        writer.WriteString("endpoint", created.Proxy.Endpoint);
        writer.WriteString("authentication", created.Proxy.Authentication);
        writer.WriteString("ticket", created.Proxy.Ticket);
        writer.WriteString("authorityPem", created.Proxy.AuthorityPem);
        writer.WriteString("authoritySha256", created.Proxy.AuthoritySha256);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>会话视图：绑定（全部主键与代次）、策略与传输、状态、租约，结束了带原因码与读句。</summary>
    private static void WriteView(Utf8JsonWriter writer, SiyuSessionView view)
    {
        var binding = view.Binding;
        writer.WriteStartObject();
        writer.WriteString("serverInstanceId", binding.ServerInstanceId);
        writer.WriteString("sessionId", binding.SessionId);
        writer.WriteNumber("userId", binding.UserId);
        writer.WriteNumber("tokenId", binding.TokenId);
        writer.WriteNumber("tokenVersion", binding.TokenVersion);
        writer.WriteNumber("officialAccountId", binding.OfficialAccountId);
        writer.WriteNumber("siteIdentityVersion", binding.SiteIdentityVersion);
        writer.WriteString("profileVersion", binding.ProfileVersion);
        writer.WriteNumber("clientProtocolVersion", binding.ClientProtocolVersion);
        writer.WriteString("claudeVersion", binding.ClaudeVersion);
        writer.WriteString("entrypoint", binding.Entrypoint);
        writer.WriteString("requestStrategy", SiyuContract.RequestStrategy);
        writer.WriteString("transport", SiyuContract.Transport);
        writer.WriteString("state", State(view.State));
        writer.WriteStartObject("lease");
        writer.WriteString("expiresAt", view.LeaseExpiresAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
        writer.WriteNumber("renewAfterSeconds", view.RenewAfterSeconds);
        writer.WriteEndObject();
        if (view.CloseCode is not null) writer.WriteString("closeCode", view.CloseCode);
        if (view.CloseSentence is not null) writer.WriteString("closeSentence", view.CloseSentence);
        writer.WriteEndObject();
    }

    /// <summary>状态的线上名字。</summary>
    private static string State(SiyuSessionState state) => state switch
    {
        SiyuSessionState.Active => "active",
        SiyuSessionState.Closing => "closing",
        SiyuSessionState.Closed => "closed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "不认识的会话状态。"),
    };
}
