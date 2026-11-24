using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Application.Contracts.Siyu;
using Application.DependencyInjection;
using Application.Interfaces.Siyu;
using Core.Certificates;
using Core.Siyu;
using HouseholdRelay.Siyu;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using static Tests.TestSuite;

namespace Tests;

/// <summary>思域产品接线：代理 CA 的存取与重签、控制面的 HTTP 与 JSON 翻译、真实组合根下控制面与代理宿主可解析并能起监听。</summary>
public static class SiyuWiringTests
{
    /// <summary>代理 CA 首次建好存进 secrets 的 siyu_proxy_ca，重开复用同一张；快到期时换新；叶证书按剩余有效期重签；站点 CA 那一份不受影响。</summary>
    public static async Task ProxyAuthorityIsStoredAndReused()
    {
        using var home = new TestHome();
        var start = DateTimeOffset.UtcNow;
        string first;
        {
            var clock = new UsageClock(start);
            var process = new TestProcess(home.Path, clock);
            var certificates = process.Get<ISiyuProxyCertificates>();
            first = certificates.Authority.Thumbprint;
            Check(!certificates.Authority.HasPrivateKey, "交给 MyCoAI 的 CA 不带私钥");
            var leaf = certificates.Leaf(OfficialHost.Api);
            Check(ReferenceEquals(leaf, certificates.Leaf(OfficialHost.Api)), "有效期够长时复用同一张叶证书");
            Check(Chains(leaf, certificates.Authority, OfficialTargets.ApiHost) && Chains(certificates.Leaf(OfficialHost.McpProxy), certificates.Authority, OfficialTargets.McpProxyHost),
                "两张叶证书都链到代理 CA、名字精确");
            clock.Advance(TimeSpan.FromDays(24));
            Check(!ReferenceEquals(leaf, certificates.Leaf(OfficialHost.Api)), "剩余不足 7 天时重签");
            await using var scope = process.Get<IServiceScopeFactory>().CreateAsyncScope();
            var secrets = scope.Repository<ProtectedSecret>();
            Check(await secrets.FirstOrDefaultAsync(item => item.Name == ProtectedSecret.SiyuProxyAuthority) is { Protected.Length: > 0 }, "CA 密文存进 siyu_proxy_ca");
            Check(await secrets.FirstOrDefaultAsync(item => item.Name == ProtectedSecret.Authority) is null, "没有碰站点 CA 那一份");
        }
        {
            var process = new TestProcess(home.Path, new UsageClock(start.AddDays(1)));
            Check(process.Get<ISiyuProxyCertificates>().Authority.Thumbprint == first, "重开后复用存着的同一张 CA");
        }
        {
            var process = new TestProcess(home.Path, new UsageClock(start.AddYears(5).AddDays(-30)));
            var stored = await StoredAuthority(process);
            Check(process.Get<ISiyuProxyCertificates>().Authority.Thumbprint != first, "CA 剩余不足 90 天时换新");
            Check(!(await StoredAuthority(process)).AsSpan().SequenceEqual(stored), "换新后的 CA 覆盖存下了");
        }
    }

    /// <summary>库里 siyu_proxy_ca 那一份密文。</summary>
    private static async Task<byte[]> StoredAuthority(TestProcess process)
    {
        await using var scope = process.Get<IServiceScopeFactory>().CreateAsyncScope();
        return (await scope.Repository<ProtectedSecret>().FirstOrDefaultAsync(item => item.Name == ProtectedSecret.SiyuProxyAuthority))!.Protected;
    }

    /// <summary>控制面：四个路径的状态码与 JSON 形状、原字节嵌入、Bearer 只认一个、严格 JSON、拒绝体与 401 challenge、409 带当前代次、没开思域回 503。</summary>
    public static async Task ControlTranslatesHttp()
    {
        var calls = new List<string>();
        var oauth = """{"accountUuid":"0f6c2a1e-3b7d-4c55-9a10-2f4e8d9b1c01", "emailAddress":"a@example.com"}"""u8.ToArray();
        var credential = """{"scopes":["user:inference"],"subscriptionType":"max"}"""u8.ToArray();
        SiyuSessionView View(SiyuSessionState state) => new(new SiyuBinding("inst", "sess-1", 1, 11, 2, 42, 7, "0123456789abcdef", 1, "2.1.325", "sdk-cli"),
            state, new DateTimeOffset(2026, 11, 22, 8, 2, 0, TimeSpan.Zero), 40, state == SiyuSessionState.Active ? null : "closed-by-client", state == SiyuSessionState.Active ? null : "已停止。");
        var closeState = SiyuSessionState.Closed;
        SiyuAccountOption[] accounts = [new(42, "家里的官方账户"), new(43, "备用")];
        var sessions = FakeProxy<ISiyuSessionService>.Create((method, args) =>
        {
            var token = (string?)args![0];
            calls.Add(method.Name + ":" + (token ?? "null"));
            if (token is null) throw new SiyuRefusal(401, "token-invalid", "个人令牌无效。");
            return method.Name switch
            {
                nameof(ISiyuSessionService.AuthorizeAsync) => Task.CompletedTask,
                nameof(ISiyuSessionService.ListAccountsAsync) => Task.FromResult<IReadOnlyList<SiyuAccountOption>>(accounts),
                nameof(ISiyuSessionService.CreateAsync) when ((SiyuCreateRequest)args[1]!).RequestId == "stale"
                    => throw new SiyuRefusal(409, "identity-version-stale", "需要重新准备。") { CurrentIdentityVersion = 9 },
                nameof(ISiyuSessionService.CreateAsync) => Task.FromResult<SiyuSessionCreated>(new SiyuOfficialSessionCreated(View(SiyuSessionState.Active), new SiyuLaunchIdentity(new string('d', 64), oauth, credential),
                    new SiyuProxyAccess("https://relay.example.test:8789/", "basic-session-ticket", "ticket-value", "-----BEGIN CERTIFICATE-----", new string('a', 64)))),
                nameof(ISiyuSessionService.ReadAsync) => Task.FromResult(View(SiyuSessionState.Active)),
                nameof(ISiyuSessionService.RenewAsync) => (long)args[2]! == 3 ? Task.FromResult(View(SiyuSessionState.Active)) : throw new InvalidOperationException("序号没传对"),
                nameof(ISiyuSessionService.CloseAsync) => Task.FromResult(View(closeState)),
                _ => throw new NotSupportedException(method.Name),
            };
        });
        var control = new SiyuControl(sessions);
        const string create = """{"requestId":"r1","officialAccountId":42,"expectedIdentityVersion":null,"clientProtocolVersion":1,"claudeVersion":"2.1.325","binarySha256":"x","entrypoint":"sdk-cli","transport":"nativeHttpsProxy"}""";

        var (status, headers, body) = await Send(control.HandleAsync, "POST", "/sessions", "Bearer tok", create);
        Check(status == 201 && headers.CacheControl == "no-store" && calls[^1] == "CreateAsync:tok", "建会话回 201、不缓存、令牌原样交给会话服务");
        Check(body.AsSpan().IndexOf(oauth) > 0 && body.AsSpan().IndexOf(credential) > 0, "oauthAccount 与凭据资料按原字节嵌入（连空格都不变）");
        using (var json = JsonDocument.Parse(body))
        {
            var root = json.RootElement;
            Check(root.GetProperty("session").GetProperty("state").GetString() == "active" && root.GetProperty("session").GetProperty("requestStrategy").GetString() == "nativePreserve"
                && root.GetProperty("session").GetProperty("lease").GetProperty("expiresAt").GetString() == "2026-11-22T08:02:00.000Z"
                && root.GetProperty("session").GetProperty("officialAccountId").GetInt64() == 42 && root.GetProperty("launchIdentity").GetProperty("deviceId").GetString() == new string('d', 64)
                && root.GetProperty("proxy").GetProperty("ticket").GetString() == "ticket-value" && root.GetProperty("proxy").GetProperty("endpoint").GetString() == "https://relay.example.test:8789/",
                "回应字段齐全、名字是驼峰");
        }

        (status, headers, body) = await Send(control.HandleAsync, "POST", "/sessions", null, create);
        Check(status == 401 && headers.WWWAuthenticate.ToString().StartsWith("Bearer", StringComparison.Ordinal) && Code(body) == "token-invalid", "没带令牌回 401 带 challenge");
        foreach (var bad in new[] { "Basic tok", "Bearer ", "Bearer a b" })
        {
            await Send(control.HandleAsync, "POST", "/sessions", bad, create);
            Check(calls[^1] == "AuthorizeAsync:null", "不合格的 Authorization 当作没带：" + bad);
        }
        (status, _, body) = await Send(control.HandleAsync, "POST", "/sessions", null, "requestId=r1", "application/x-www-form-urlencoded");
        Check(status == 401 && Code(body) == "token-invalid" && calls[^1] == "AuthorizeAsync:null", "先鉴权再读请求体：没带令牌又不是 JSON 回 401，不是 415");
        (status, _, body) = await Send(control.HandleAsync, "PUT", "/sessions/sess-1/lease", null, "x", "text/plain");
        Check(status == 401 && calls[^1] == "AuthorizeAsync:null", "续租同样先鉴权");
        var before = calls.Count(call => call.StartsWith("CreateAsync:", StringComparison.Ordinal));
        foreach (var (label, text, type, code) in new[]
        {
            ("多出字段", create.Replace("}", ",\"extra\":1}", StringComparison.Ordinal), "application/json", "request-malformed"),
            ("缺字段", create.Replace("\"entrypoint\":\"sdk-cli\",", "", StringComparison.Ordinal), "application/json", "request-malformed"),
            ("数字写成字符串", create.Replace("42", "\"42\"", StringComparison.Ordinal), "application/json", "request-malformed"),
            ("不是 JSON", "requestId=r1", "application/x-www-form-urlencoded", "content-type"),
            ("太大", create.Replace("\"x\"", "\"" + new string('x', SiyuControl.MaxBodyBytes) + "\"", StringComparison.Ordinal), "application/json", "body-too-large"),
        })
        {
            (status, _, body) = await Send(control.HandleAsync, "POST", "/sessions", "Bearer tok", text, type);
            Check(Code(body) == code && status is 400 or 413 or 415, $"{label}：应回 {code}，实际 {status} {Code(body)}");
        }
        Check(calls.Count(call => call.StartsWith("CreateAsync:", StringComparison.Ordinal)) == before && calls[^1] == "AuthorizeAsync:tok", "请求体不合合同时只鉴权、不进建会话");

        (status, _, body) = await Send(control.HandleAsync, "POST", "/sessions", "Bearer tok", create.Replace("\"r1\"", "\"stale\"", StringComparison.Ordinal));
        using (var json = JsonDocument.Parse(body))
            Check(status == 409 && json.RootElement.GetProperty("error").GetProperty("type").GetString() == "siyu_refused"
                && json.RootElement.GetProperty("error").GetProperty("currentIdentityVersion").GetInt64() == 9, "身份代次过时回 409 带当前代次");

        (status, _, _) = await Send(control.HandleAsync, "GET", "/sessions/sess-1", "Bearer tok", null);
        Check(status == 200 && calls[^1] == "ReadAsync:tok", "查询回 200");
        (status, _, _) = await Send(control.HandleAsync, "PUT", "/sessions/sess-1/lease", "Bearer tok", """{"leaseSequence":3}""");
        Check(status == 200 && calls[^1] == "RenewAsync:tok", "续租回 200，序号原样交给会话服务");
        (status, _, body) = await Send(control.HandleAsync, "DELETE", "/sessions/sess-1", "Bearer tok", null);
        Check(status == 204 && body.Length == 0, "已 Closed 的停止回 204、无正文");
        closeState = SiyuSessionState.Closing;
        (status, _, body) = await Send(control.HandleAsync, "DELETE", "/sessions/sess-1", "Bearer tok", null);
        Check(status == 202 && JsonDocument.Parse(body).RootElement.GetProperty("state").GetString() == "closing", "还在收尾的停止回 202 带 closing");
        foreach (var (method, path) in new[] { ("GET", "/sessions"), ("POST", "/sessions/sess-1"), ("PUT", "/sessions/sess-1/other"), ("PATCH", "/sessions/sess-1"), ("GET", "/sessions/a/b/c"),
            ("GET", ""), ("GET", "/sess-1"), ("POST", "/accounts"), ("GET", "/accounts/42"), ("GET", "/other") })
        {
            (status, _, body) = await Send(control.HandleAsync, method, path, "Bearer tok", null);
            Check(status == 404 && Code(body) == "not-found", $"{method} {path} 不是控制面地址");
        }

        (status, headers, body) = await Send(control.HandleAsync, "GET", "/accounts", "Bearer tok", null);
        Check(status == 200 && headers.CacheControl == "no-store" && calls[^1] == "ListAccountsAsync:tok", "账户列表回 200、不缓存、令牌原样交给会话服务");
        using (var json = JsonDocument.Parse(body))
        {
            var root = json.RootElement;
            var items = root.GetProperty("accounts").EnumerateArray().ToArray();
            Check(root.EnumerateObject().Count() == 1 && items.Length == 2 && items.All(item => item.EnumerateObject().Select(p => p.Name).SequenceEqual(["officialAccountId", "displayName"]))
                && items[0].GetProperty("officialAccountId").GetInt64() == 42 && items[0].GetProperty("displayName").GetString() == "家里的官方账户"
                && items[1].GetProperty("officialAccountId").GetInt64() == 43, "账户列表形状是 {accounts:[{officialAccountId,displayName}]}，只有这两个字段");
        }
        accounts = [];
        (status, _, body) = await Send(control.HandleAsync, "GET", "/accounts", "Bearer tok", null);
        Check(status == 200 && Encoding.UTF8.GetString(body) == """{"accounts":[]}""", "一个都没有回空数组，不回 404");
        (status, headers, body) = await Send(control.HandleAsync, "GET", "/accounts", null, null);
        Check(status == 401 && headers.WWWAuthenticate.ToString().StartsWith("Bearer", StringComparison.Ordinal) && Code(body) == "token-invalid", "账户列表没带令牌回 401，拒绝形状与控制面一致");

        foreach (var (method, path) in new[] { ("POST", "/sessions"), ("GET", "/accounts") })
        {
            (status, headers, body) = await Send(SiyuControl.DisabledAsync, method, path, "Bearer tok", method == "POST" ? create : null);
            Check(status == 503 && Code(body) == "siyu-disabled" && headers.CacheControl == "no-store", $"没开思域时 {method} {path} 回 503 并读出怎么打开");
        }
    }

    /// <summary>
    /// 控制面的版本 2 形状：官方与标准两种建会话回应、会话视图带 mode（标准不写官方专属的三个键）、拒绝体带打回（只有 action 与有值的那一项）、续租把声明的版本交给会话服务；
    /// 版本 1 的会话视图与拒绝体逐字节与以前相同，版本 1 不带账户仍按看不懂处理、不进建会话。
    /// </summary>
    public static async Task ControlWritesVersion2()
    {
        var calls = new List<string>();
        var control = new SiyuControl(Version2Service(calls));
        var v1 = """{"requestId":"v1","officialAccountId":42,"expectedIdentityVersion":null,"clientProtocolVersion":1,"claudeVersion":"2.1.325","binarySha256":"x","entrypoint":"sdk-cli","transport":"nativeHttpsProxy"}""";

        var (status, _, body) = await Send(control.HandleAsync, "GET", "/sessions/v1", "Bearer tok", null);
        Check(status == 200 && Encoding.UTF8.GetString(body) == """{"serverInstanceId":"inst","sessionId":"v1","userId":1,"tokenId":11,"tokenVersion":2,"officialAccountId":42,"siteIdentityVersion":7,"profileVersion":"0123456789abcdef","clientProtocolVersion":1,"claudeVersion":"2.1.325","entrypoint":"sdk-cli","requestStrategy":"nativePreserve","transport":"nativeHttpsProxy","state":"active","lease":{"expiresAt":"2026-11-22T08:02:00.000Z","renewAfterSeconds":40}}""",
            "版本 1 的会话视图逐字节与以前相同（没有 mode）：" + Encoding.UTF8.GetString(body));
        (status, _, body) = await Send(control.HandleAsync, "POST", "/sessions", "Bearer tok", v1.Replace("\"v1\"", "\"stale1\"", StringComparison.Ordinal));
        Check(status == 409 && Encoding.UTF8.GetString(body) == """{"type":"error","error":{"type":"siyu_refused","code":"identity-version-stale","message":"stale","currentIdentityVersion":9}}""",
            "版本 1 的过时代次拒绝逐字节与以前相同：" + Encoding.UTF8.GetString(body));
        var before = calls.Count(call => call.StartsWith("CreateAsync", StringComparison.Ordinal));
        foreach (var bad in new[] { v1.Replace("\"officialAccountId\":42,", "", StringComparison.Ordinal), v1.Replace("42", "null", StringComparison.Ordinal) })
        {
            (status, _, body) = await Send(control.HandleAsync, "POST", "/sessions", "Bearer tok", bad);
            Check(status == 400 && Code(body) == "request-malformed", "版本 1 不带账户或带 null：与以前一样 400 request-malformed");
        }
        Check(calls.Count(call => call.StartsWith("CreateAsync", StringComparison.Ordinal)) == before, "版本 1 不带账户不进建会话");

        var responses = await Version2ResponsesAsync(control);
        using (var json = JsonDocument.Parse(responses["建会话·官方"]))
            Check(json.RootElement.GetProperty("session").GetProperty("mode").GetString() == "official" && json.RootElement.GetProperty("proxy").GetProperty("ticket").GetString() == "ticket-value",
                "版本 2 官方建会话：视图带 mode official，照旧带代理接入");
        using (var json = JsonDocument.Parse(responses["建会话·标准"]))
        {
            var root = json.RootElement;
            Check(root.GetProperty("session").GetProperty("mode").GetString() == "standard" && !root.GetProperty("session").TryGetProperty("officialAccountId", out _)
                && root.GetProperty("standard").GetProperty("baseUrl").GetString() == "https://relay.example.test:8787/" && !root.TryGetProperty("proxy", out _) && !root.TryGetProperty("launchIdentity", out _),
                "版本 2 标准建会话：只有标准 API 接入，没有票据、身份与官方账户");
        }
        Check(Encoding.UTF8.GetString(responses["拒绝·recreate"]).EndsWith(""","remedy":{"action":"recreate","expectedIdentityVersion":9}}}""", StringComparison.Ordinal),
            "版本 2 过时代次：打回 recreate 带期望代次，不再另带 currentIdentityVersion");
        Check(Encoding.UTF8.GetString(responses["拒绝·wait"]).EndsWith(""","remedy":{"action":"wait","retryAfterSeconds":30}}}""", StringComparison.Ordinal), "版本 2 维护中：打回 wait 30 秒");
        Check(!Encoding.UTF8.GetString(responses["拒绝·不打回"]).Contains("remedy", StringComparison.Ordinal), "要人处理的拒绝不带打回");
        Check(calls.Contains("RenewAsync:tok:2"), "续租把请求声明的版本交给会话服务");
        (status, _, _) = await Send(control.HandleAsync, "PUT", "/sessions/v1/lease", "Bearer tok", """{"leaseSequence":3}""");
        Check(status == 200 && calls[^1] == "RenewAsync:tok:1", "续租不带版本按版本 1");
    }

    /// <summary>
    /// 版本 2 控制面会回的每一种形状各一份（协议文本「契约表:版本2键」逐项对照）：建会话官方、标准，会话视图官方、标准（已结束，键最全），拒绝 recreate、wait、不打回。
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, byte[]>> Version2ResponsesAsync(SiyuControl? control = null)
    {
        control ??= new SiyuControl(Version2Service([]));
        string Create(string requestId) => """{"requestId":"ID","expectedIdentityVersion":null,"clientProtocolVersion":2,"claudeVersion":"2.1.325","binarySha256":"x","entrypoint":"sdk-cli","transport":"nativeHttpsProxy"}""".Replace("ID", requestId, StringComparison.Ordinal);
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["建会话·官方"] = (await Send(control.HandleAsync, "POST", "/sessions", "Bearer tok", Create("official"))).Body,
            ["建会话·标准"] = (await Send(control.HandleAsync, "POST", "/sessions", "Bearer tok", Create("standard"))).Body,
            ["会话视图·官方"] = (await Send(control.HandleAsync, "GET", "/sessions/official-closed", "Bearer tok", null)).Body,
            ["会话视图·标准"] = (await Send(control.HandleAsync, "GET", "/sessions/standard-closed", "Bearer tok", null)).Body,
            ["拒绝·recreate"] = (await Send(control.HandleAsync, "POST", "/sessions", "Bearer tok", Create("stale2"))).Body,
            ["拒绝·wait"] = (await Send(control.HandleAsync, "PUT", "/sessions/maint/lease", "Bearer tok", """{"leaseSequence":1,"clientProtocolVersion":2}""")).Body,
            ["拒绝·不打回"] = (await Send(control.HandleAsync, "POST", "/sessions", "Bearer tok", Create("none"))).Body,
        };
    }

    /// <summary>按请求编号或会话编号回版本 1、版本 2 各种结果的假会话服务；拒绝先经 <see cref="SiyuRefusal.ForProtocol"/>，与真服务同一条路。</summary>
    private static ISiyuSessionService Version2Service(List<string> calls)
    {
        var expires = new DateTimeOffset(2026, 11, 22, 8, 2, 0, TimeSpan.Zero);
        SiyuBinding Binding(string id, long? account, int version) => new("inst", id, 1, 11, 2, account, 7, account is null ? "" : "0123456789abcdef", version, "2.1.325", "sdk-cli");
        SiyuSessionView Active(string id, long? account, int version) => new(Binding(id, account, version), SiyuSessionState.Active, expires, 40);
        SiyuSessionView Closed(string id, long? account) => new(Binding(id, account, 2), SiyuSessionState.Closed, expires, 40, "account-switched", "switched");
        return FakeProxy<ISiyuSessionService>.Create((method, args) =>
        {
            var token = (string?)args![0];
            return method.Name switch
            {
                nameof(ISiyuSessionService.AuthorizeAsync) => Task.CompletedTask,
                nameof(ISiyuSessionService.CreateAsync) => Create((SiyuCreateRequest)args[1]!),
                nameof(ISiyuSessionService.ReadAsync) => Task.FromResult((string)args[1]! switch
                {
                    "official-closed" => Closed("official-closed", 42),
                    "standard-closed" => Closed("standard-closed", null),
                    var id => Active(id, 42, 1),
                }),
                nameof(ISiyuSessionService.RenewAsync) => Renew((string)args[1]!, (int)args[3]!),
                _ => throw new NotSupportedException(method.Name),
            };

            Task<SiyuSessionCreated> Create(SiyuCreateRequest request)
            {
                calls.Add("CreateAsync:" + token);
                var refusal = request.RequestId switch
                {
                    "stale1" or "stale2" => new SiyuRefusal(409, "identity-version-stale", "stale") { CurrentIdentityVersion = 9 },
                    "none" => new SiyuRefusal(503, "no-usable-upstream", "none"),
                    _ => null,
                };
                if (refusal is not null) throw refusal.ForProtocol(request.ClientProtocolVersion);
                return Task.FromResult<SiyuSessionCreated>(request.RequestId == "standard"
                    ? new SiyuStandardSessionCreated(Active("s-standard", null, 2), new SiyuStandardAccess("https://relay.example.test:8787/", SiyuContract.StandardProtocol, SiyuContract.StandardAuthentication))
                    : new SiyuOfficialSessionCreated(Active("s-official", 42, request.ClientProtocolVersion), new SiyuLaunchIdentity(new string('d', 64), """{"accountUuid":"u"}"""u8.ToArray(), """{"scopes":[]}"""u8.ToArray()),
                        new SiyuProxyAccess("https://relay.example.test:8789/", "basic-session-ticket", "ticket-value", "-----BEGIN CERTIFICATE-----", new string('a', 64))));
            }

            Task<SiyuSessionView> Renew(string id, int version)
            {
                calls.Add($"RenewAsync:{token}:{version}");
                return id == "maint" ? throw new SiyuRefusal(503, "site-maintenance", "maint").ForProtocol(version) : Task.FromResult(Active(id, 42, version));
            }
        });
    }

    /// <summary>
    /// 真实组合根：会话服务与准入门是同一份；控制面、代理宿主可以解析；代理宿主用真实代理 CA、真实准入起在回环随机端口，
    /// 外层 TLS 用站点证书、只说 HTTP/1.1，没有或错的票据回 407。没填对外地址时会话服务建不起来并读出下一步。
    /// </summary>
    public static async Task ProductWiringServesProxy()
    {
        using var home = new TestHome();
        var process = new TestProcess(home.Path);
        Assert(() => process.Get<Lazy<SiyuControl>>().Value, "对外 HTTPS 地址");
        process = new TestProcess(home.Path);
        var settings = await process.Settings.ReadAsync(); settings.Value.PublicUrl = "https://relay.example.test:8787"; settings.Value.SiyuEnabled = true;
        await process.Settings.SaveAsync(settings.Version, settings.Value);
        process = new TestProcess(home.Path);
        Check(ReferenceEquals(process.Get<ISiyuGate>(), process.Get<ISiyuSessionService>()), "准入门就是会话服务");
        _ = process.Get<Lazy<SiyuControl>>().Value;

        var factory = TestProcess.CertificateFactory;
        using var issued = factory.CreateCertificate("localhost", out var sitePem);
        using var site = factory.LoadServerCertificate(issued.Export(X509ContentType.Pkcs12));
        await using var proxy = process.Get<Lazy<SiyuProxyHost>>().Value.Build(site, null, testPort: 0);
        await proxy.StartAsync();
        var port = new Uri(proxy.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
        using var authority = X509Certificate2.CreateFromPem(sitePem);
        foreach (var credentials in new[] { null, SiyuProxyRig.Basic("sess-unknown", "wrong") })
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            await using var outer = new SslStream(tcp.GetStream(), false);
            await outer.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
                RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null && Chains(X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()), authority, "localhost"),
            });
            Check(outer.NegotiatedApplicationProtocol == SslApplicationProtocol.Http11, "外层只说 HTTP/1.1");
            var target = OfficialTargets.Authority(OfficialHost.Api);
            var auth = credentials is null ? "" : $"Proxy-Authorization: Basic {credentials}\r\n";
            await outer.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n{auth}\r\n"));
            var reply = new byte[1024];
            var read = await outer.ReadAsync(reply);
            Check(Encoding.ASCII.GetString(reply, 0, read).StartsWith("HTTP/1.1 407 ", StringComparison.Ordinal), "没有或错的票据回 407：" + (credentials is null ? "无" : "错"));
        }
        await proxy.StopAsync();
    }

    /// <summary>以只信 <paramref name="authority"/> 的方式核链，并要求名字精确匹配。</summary>
    private static bool Chains(X509Certificate2 leaf, X509Certificate2 authority, string name)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf) && leaf.MatchesHostname(name, allowWildcards: false, allowCommonName: false);
    }

    /// <summary>在内存里走一次控制面：路径是去掉前缀后的部分。</summary>
    internal static async Task<(int Status, IHeaderDictionary Headers, byte[] Body)> Send(RequestDelegate handler, string method, string path, string? authorization, string? body,
        string contentType = "application/json")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method; context.Request.PathBase = SiyuControl.Prefix; context.Request.Path = path;
        if (authorization is not null) context.Request.Headers.Authorization = authorization;
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes); context.Request.ContentLength = bytes.Length; context.Request.ContentType = contentType;
        }
        var response = new MemoryStream();
        context.Response.Body = response;
        await handler(context);
        return (context.Response.StatusCode, context.Response.Headers, response.ToArray());
    }

    /// <summary>错误体里的原因码；不是错误体时为 null。</summary>
    private static string? Code(byte[] body)
    {
        if (body.Length == 0) return null;
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null;
    }

    /// <summary>期待抛出读句含 <paramref name="fragment"/> 的 <see cref="InvalidOperationException"/>。</summary>
    private static void Assert(Func<object> action, string fragment)
    {
        try { action(); Check(false, "应拒绝：" + fragment); }
        // 预期的拒绝：核对读句。
        catch (InvalidOperationException error) { Check(error.Message.Contains(fragment, StringComparison.Ordinal), "读句应含「" + fragment + "」：" + error.Message); }
    }
}
