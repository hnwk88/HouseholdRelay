using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Application.Contracts.Upstream;
using Application.Interfaces.Certificates;
using Application.Interfaces.Configuration;
using Application.Interfaces.Identity;
using Application.Interfaces.Relay;
using Application.Interfaces.Upstream;
using Application.Interfaces.Usage;
using Application.Services.Identity;
using Application.Services.Upstream;
using Application.Services.Usage;
using Core.Certificates;
using Core.Configuration;
using Core.Upstream;
using HouseholdRelay.Commands;
using Infrastructure.Logging;
using Infrastructure.Persistence;
using Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static Tests.TestSuite;

namespace Tests;

/// <summary>
/// 订单 148「稳定与排查」的用例：检测一定落到终态、后台检测可查可取消、兜底异常留一行只有类型阶段编号的日志、事件日志写不出去不影响请求、
/// 私有 CA 私钥留存后重签不换根、覆盖前留副本能退回、到期前 30 天提示、/healthz 不变而 /readyz 如实、自动切换新实例默认关，以及几处小修。
/// </summary>
static class StabilityTests
{
    /// <summary>放在请求里的假令牌：日志里全文搜它必须是 0 处。</summary>
    private const string FakeToken = "sk-fixture-fake-token-1480000";

    /// <summary>P-006/P-031/P-111：非在用的一家获取模型、测试时解密出错（标记检测中之后的意外异常），落到「检测未完成」，不停在检测中，能删。</summary>
    public static async Task DetectionEndsAfterFault()
    {
        await using var mock = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        foreach (var test in new[] { false, true })
        {
            var id = await web.Upstreams.SaveCandidate((await web.Upstreams.ReadActive()).ActiveVersion, mock.Options(), "fixture-fault-secret");
            web.Database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, "UPDATE upstream_candidates SET credential=$value WHERE id=$id", ("$value", new byte[] { 1, 2, 3 }), ("$id", id)));
            Exception? thrown = null;
            try { if (test) await web.Upstreams.TestCandidate(id, true, CancellationToken.None); else await web.Upstreams.DiscoverModels(id, CancellationToken.None); }
            // 解密损坏的凭据要出错，这里只收下来核对类型。
            catch (Exception error) { thrown = error; }
            var view = await web.Upstreams.ReadCandidate(id);
            Check(thrown is not null and not InvalidOperationException, (test ? "test: " : "discovery: ") + thrown?.GetType());
            Check(view.State == CandidateState.Untested && view.Phase == UpstreamCheckPhase.Idle && view.Deletable
                && view.Report?.Message == UpstreamReport.UnfinishedLead + UpstreamReport.FaultReason + UpstreamReport.UnfinishedNext, (test ? "test: " : "discovery: ") + view.Report?.Message);
        }
    }

    /// <summary>P-006/P-031/P-032：非在用的一家获取模型、测试途中被取消，落到「检测未完成：管理员取消了这次检测」；取消前读得出「正在检测」。</summary>
    public static async Task DetectionEndsAfterCancel()
    {
        await using var mock = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        foreach (var test in new[] { false, true })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            mock.Handler = async context => { entered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, context.RequestAborted); } catch (OperationCanceledException) { /* 模拟不应答；取消后请求中止属于预期。 */ } };
            var id = await web.Upstreams.SaveCandidate((await web.Upstreams.ReadActive()).ActiveVersion, mock.Options(), "fixture-cancel-secret");
            using var cancellation = new CancellationTokenSource();
            Task pending = test ? web.Upstreams.TestCandidate(id, true, cancellation.Token) : web.Upstreams.DiscoverModels(id, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Check((await web.Upstreams.ReadCandidate(id)).Phase == UpstreamCheckPhase.Checking, "A running check reads as checking");
            await cancellation.CancelAsync();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(30)); }
            // 取消以异常结束也算，核对的是落库的终态。
            catch (OperationCanceledException) { }
            var view = await web.Upstreams.ReadCandidate(id);
            Check(view.State == CandidateState.Untested && view.Phase == UpstreamCheckPhase.Idle
                && view.Report?.Message == UpstreamReport.UnfinishedLead + UpstreamReport.CancelledReason + UpstreamReport.UnfinishedNext, (test ? "test: " : "discovery: ") + view.Report?.Message);
        }
    }

    /// <summary>P-073：页面判「正在检测」「检测未完成」只读检测阶段——页面代码里按原始状态判检测中的地方 0 处；停在检测中却已过窗口的读「检测未完成」，能删。</summary>
    public static async Task PagesReadPhaseOnly()
    {
        var pages = Path.Combine(RepositoryRoot(), "src", "HouseholdRelay", "Pages");
        var raw = Directory.EnumerateFiles(pages, "*.cs*", SearchOption.AllDirectories).Where(file => File.ReadAllText(file).Contains("CandidateState.Testing", StringComparison.Ordinal)).ToArray();
        Check(raw.Length == 0, "Pages read the raw testing state: " + string.Join(", ", raw));
        await using var mock = await UpstreamFixture.Create(); await using var web = await WebFixture.Create(); await web.Login();
        var id = await web.Upstreams.SaveCandidate((await web.Upstreams.ReadActive()).ActiveVersion, mock.Options(), "fixture-window-secret");
        web.Database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, "UPDATE upstream_candidates SET state='testing', test_started_at=$at WHERE id=$id",
            ("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - UpstreamCandidate.TestWindowSeconds - 5), ("$id", id)));
        var view = await web.Upstreams.ReadCandidate(id);
        Check(view.Phase == UpstreamCheckPhase.Interrupted && view.Deletable);
        using var page = await web.Http.GetAsync(web.Url + "/Upstream");
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        Check(html.Contains(UpstreamReport.UnfinishedLead + UpstreamReport.WindowReason) && !html.Contains("正在检测，"), "Interrupted check must read as unfinished");
    }

    /// <summary>P-076：页面提交只建后台检测、马上回来；按编号查得到「进行中」，按「取消检测」后读出「检测未完成」；服务重启时残留的检测中写成终态。</summary>
    public static async Task BackgroundCheckCanBeCancelled()
    {
        await using var mock = await UpstreamFixture.Create();
        mock.Handler = async context => { try { await Task.Delay(Timeout.Infinite, context.RequestAborted); } catch (OperationCanceledException) { /* 模拟不应答；取消后请求中止属于预期。 */ } };
        await using var web = await WebFixture.Create(); await web.Login();
        var id = await web.Upstreams.SaveCandidate((await web.Upstreams.ReadActive()).ActiveVersion, mock.Options(), "fixture-background-secret");
        var started = await RawPost(web, "/Upstream?handler=Rediscover", new() { ["candidateId"] = id.ToString(), ["__RequestVerificationToken"] = await web.Csrf("/Upstream") });
        var match = System.Text.RegularExpressions.Regex.Match(started, "data-check-id=\"(\\d+)\"");
        Check(match.Success && started.Contains("aria-live=\"polite\"") && started.Contains("取消检测"), "The page must return at once with a status region");
        var check = match.Groups[1].Value;
        using (var state = await web.Http.GetAsync(web.Url + "/Upstream?handler=Check&checkId=" + check))
            Check((await state.Content.ReadAsStringAsync()).Contains("\"ended\":false"), "A hanging check is still running");
        var cancelled = await RawPost(web, "/Upstream?handler=CancelCheck", new() { ["checkId"] = check, ["__RequestVerificationToken"] = await web.Csrf("/Upstream") });
        Check(cancelled.Contains(UpstreamReport.UnfinishedLead + UpstreamReport.CancelledReason), "Cancel must read the unfinished conclusion");
        Check((await web.Upstreams.ReadCandidate(id)).Report?.Message.StartsWith(UpstreamReport.UnfinishedLead + UpstreamReport.CancelledReason, StringComparison.Ordinal) == true);
        web.Database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, "UPDATE upstream_candidates SET state='testing', test_started_at=$at WHERE id=$id",
            ("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$id", id)));
        await web.App.Services.GetRequiredService<UpstreamChecks>().StartAsync(CancellationToken.None);
        Check((await web.Upstreams.ReadCandidate(id)).Report?.Message.StartsWith(UpstreamReport.UnfinishedLead + UpstreamReport.RestartReason, StringComparison.Ordinal) == true, "Restart must end a stale check");
    }

    /// <summary>
    /// P-012/P-020（中转、管理页两处兜底）：制造一次未预料的异常，日志文件里各出现一行只含异常类型、阶段、请求编号的记录；中转错误体带同一个请求编号；
    /// 日志里没有个人令牌、假令牌和异常消息。
    /// </summary>
    public static async Task HostFaultsLeaveOneLine()
    {
        var failStart = false;
        await using var web = await WebFixture.Create(overrides: services =>
        {
            services.AddSingleton<IUsageService>(provider => FakeProxy<IUsageService>.Wrap(ActivatorUtilities.CreateInstance<UsageService>(provider),
                method => failStart && method.Name == nameof(IUsageService.Start) ? new NotSupportedException("fixture-relay-fault") : null));
            services.AddSingleton<ICertificateService>(provider => FakeProxy<ICertificateService>.Wrap(ActivatorUtilities.CreateInstance<CertificateService>(provider),
                method => method.Name == nameof(ICertificateService.ReadInfo) ? new NotSupportedException("fixture-management-fault") : null));
        });
        var person = await web.Identities.Create("故障"); failStart = true;
        using var request = new HttpRequestMessage(HttpMethod.Post, web.Url + "/v1/messages") { Content = new StringContent("{\"model\":\"fixture-model\",\"note\":\"" + FakeToken + "\"}", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new("Bearer", person.Token); request.Headers.Add("x-fixture-note", FakeToken);
        using var response = await web.Http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var requestId = JsonDocument.Parse(body).RootElement.GetProperty("request_id").GetString();
        Check(response.StatusCode == HttpStatusCode.InternalServerError && requestId?.Length == 32, body);
        await web.Login();
        using (var page = await web.Http.GetAsync(web.Url + "/")) Check(page.StatusCode == HttpStatusCode.ServiceUnavailable);
        var lines = await LogLines(web.Home.Path, 2, line => line.Contains("\"stage\":\"relay\"") || line.Contains("\"stage\":\"management\""));
        var relay = lines.Single(line => line.Contains("\"stage\":\"relay\""));
        Check(relay.Contains("\"requestId\":\"" + requestId + "\"") && relay.Contains("\"exceptionType\":\"System.NotSupportedException\""), relay);
        Check(lines.Single(line => line.Contains("\"stage\":\"management\"")).Contains("System.NotSupportedException"));
        NoSecrets(web.Home.Path, person.Token, "fixture-relay-fault", "fixture-management-fault");
    }

    /// <summary>P-012（证书加载那一处）：证书加载失败时控制台读句不变，日志里一行类型与阶段 serve:certificate；管理宿主照常启动。</summary>
    public static async Task ServeCertificateFaultLeavesOneLine()
    {
        using var home = new TestHome();
        await using var admin = HouseholdRelay.Management.ManagementWeb.Build(home.Path, 0, services =>
            services.AddSingleton<ICertificateService>(provider => FakeProxy<ICertificateService>.Wrap(ActivatorUtilities.CreateInstance<CertificateService>(provider),
                method => method.Name == nameof(ICertificateService.LoadForServing) ? new CryptographicException("fixture-certificate-fault") : null)));
        var lifetime = admin.Services.GetRequiredService<IHostApplicationLifetime>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        var running = admin.Services.GetRequiredService<ServeCommand>().Run(admin, 0);
        var line = (await LogLines(home.Path, 1, text => text.Contains("\"stage\":\"serve:certificate\""))).Single();
        Check(line.Contains("System.Security.Cryptography.CryptographicException"), line);
        // 管理宿主照常启动完成后再叫停：证书失败只让对外 HTTPS 不启用。
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        lifetime.StopApplication();
        await running.WaitAsync(TimeSpan.FromSeconds(20));
        NoSecrets(home.Path, "fixture-certificate-fault");
    }

    /// <summary>P-012（命令行那一处）：未预料的异常返回退出码 4，控制台只读通用句，日志里一行类型与阶段 command:子命令。</summary>
    public static async Task CommandFaultLeavesOneLine()
    {
        var errors = RelayLogFiles.Errors(AppContext.BaseDirectory).Path;
        var before = File.Exists(errors) ? ReadShared(errors).Length : 0;
        // 数据目录里带空字符：Path.GetFullPath 抛 ArgumentException，是命令行分类之外的异常；数据目录没定下来，日志落在程序目录。
        var code = await HouseholdRelay.Program.Main(["clients", "list", "--home", "bad\0home"]);
        var added = ReadShared(errors)[before..];
        Check(code == ExitCodes.Unexpected && added.Contains("\"stage\":\"command:clients\"") && added.Contains("System.ArgumentException") && !added.Contains("bad"), added);
    }

    /// <summary>P-085/P-117：事件日志的控制台写不出去时请求照常结束、事件照样落盘；中文不转义；文件限 5 MB、留 3 份轮换（用小上限验证轮换）。</summary>
    public static async Task EventLogSurvivesAndRotates()
    {
        Check(RotatingLogFile.MaxBytes == 5 * 1024 * 1024 && RotatingLogFile.Keep == 3, "The documented limit is 5 MB and 3 files");
        using var home = new TestHome();
        var file = new RotatingLogFile(Path.Combine(home.Path, "events.log"), RotatingLogFile.MaxBytes, RotatingLogFile.Keep);
        var events = new ServiceRelayEventLog(file, new BrokenWriter());
        await using var mock = await UpstreamFixture.Create();
        await using (var web = await WebFixture.Create(overrides: services => services.AddSingleton<IRelayEventLog>(events)))
        {
            await mock.Activate(web.Upstreams); var person = await web.Identities.Create("书房");
            using var response = await UpstreamFixture.RelayRequest(web, person.Token);
            Check(response.StatusCode == HttpStatusCode.OK, "A broken console must not fail the request");
        }
        events.Write(new { upstream = "顺丰中转" });
        var text = ReadShared(file.Path);
        Check(events.ConsoleFailures >= 2 && text.Contains("\"upstream\":\"顺丰中转\"") && !text.Contains("\\u"), text);
        var small = new RotatingLogFile(Path.Combine(home.Path, "small.log"), 300, 3);
        for (var index = 0; index < 40; index++) small.Append(new string('x', 40) + index);
        Check(File.Exists(small.Path) && File.Exists(small.Rotated(1)) && File.Exists(small.Rotated(2)) && !File.Exists(small.Rotated(3)), "Rotation keeps three files");
        Check(new[] { small.Path, small.Rotated(1), small.Rotated(2) }.All(path => new FileInfo(path).Length <= 300) && small.Failures == 0);
    }

    /// <summary>P-021：用存下来的私有 CA 换地址重签服务器证书，CA 指纹不变、服务器证书换了；旧版建的（没存 CA 私钥）不能重签，读出下一步，HTTPS 页说清楚。</summary>
    public static async Task ReissueKeepsAuthority()
    {
        await using var web = await WebFixture.Create(); var certificates = web.Certificates; var settings = web.Settings;
        await certificates.CreatePrivate((await settings.Read()).Version, "https://localhost:8787", 8787);
        var fingerprint = await certificates.AuthorityFingerprint(); var first = (await certificates.ReadInfo())!;
        Check(await certificates.CanReissue());
        await certificates.ReissuePrivate((await settings.Read()).Version, "https://127.0.0.1:8787", 8787);
        var second = (await certificates.ReadInfo())!;
        Check(await certificates.AuthorityFingerprint() == fingerprint && second.Thumbprint != first.Thumbprint && second.Host == "127.0.0.1", "Reissue keeps the root and replaces the leaf");
        using (var serving = await certificates.LoadForServing()) Check(serving is not null);
        web.Database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, "DELETE FROM secrets WHERE name='ca'"));
        Check(!await certificates.CanReissue());
        var refused = await Refusal(() => certificates.ReissuePrivate(1, "https://localhost:8787", 8787));
        Check(refused.Contains("旧版建的私有 CA"), refused);
        await web.Login();
        using var page = await web.Http.GetAsync(web.Url + "/Https");
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        Check(html.Contains("这份私有 CA 是旧版建的，CA 私钥当初没有留存") && !html.Contains("用现有私有 CA 重签服务器证书（"), "The page must say a legacy authority cannot be reissued");
    }

    /// <summary>P-024/P-103：已有私有 CA 时不勾确认项的导入被服务端拒绝；导入覆盖前留旧副本，退回后私有 CA、模式与地址都回来，再退回一次回到导入的那份。</summary>
    public static async Task ImportKeepsPrevious()
    {
        await using var web = await WebFixture.Create(); var certificates = web.Certificates; var settings = web.Settings;
        await certificates.CreatePrivate((await settings.Read()).Version, "https://localhost:8787", 8787);
        var fingerprint = await certificates.AuthorityFingerprint();
        await web.Login();
        using (var page = await web.Http.GetAsync(web.Url + "/Https"))
            Check(WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync()).Contains("我确认用导入的证书替换现在的私有 CA"), "The import form must show the confirmation");
        using var generated = TestProcess.CertificateFactory.CreateCertificate("localhost", out var pem); using var authority = X509Certificate2.CreateFromPem(pem);
        var pfx = generated.Export(X509ContentType.Pkcs12, "fixture-pfx-password");
        var current = (await settings.Read()).Version;
        var refused = await Refusal(() => certificates.Import(current, "https://localhost:5433", 8787, pfx, "fixture-pfx-password", false));
        Check(refused.Contains("请先勾选") && await certificates.AuthorityFingerprint() == fingerprint, refused);
        await certificates.Import((await settings.Read()).Version, "https://localhost:5433", 8787, pfx, "fixture-pfx-password", new X509Certificate2Collection(authority));
        Check(await certificates.AuthorityFingerprint() is null && (await certificates.ReadInfo())?.Mode == "trusted" && await certificates.HasPrevious());
        await certificates.RestorePrevious((await settings.Read()).Version);
        Check(await certificates.AuthorityFingerprint() == fingerprint && (await certificates.ReadInfo())?.Mode == "private" && (await settings.Read()).Value.PublicUrl == "https://localhost:8787"
            && await certificates.CanReissue(), "Restore brings back the private authority and its address");
        using (var serving = await certificates.LoadForServing()) Check(serving is not null);
        await certificates.RestorePrevious((await settings.Read()).Version);
        Check((await certificates.ReadInfo())?.Mode == "trusted" && (await settings.Read()).Value.PublicUrl == "https://localhost:5433", "Restoring again returns to the imported one");
    }

    /// <summary>P-074：证书到期前 30 天三页（概览、HTTPS、连接说明）都提示，第 31 天不提示。</summary>
    public static async Task ExpiryWarnsThirtyDaysAhead()
    {
        var now = DateTimeOffset.UtcNow;
        Check(!new CertificateInfo("private", "localhost", now.AddDays(31), "x", now).ExpiringSoon(now) && new CertificateInfo("private", "localhost", now.AddDays(30), "x", now).ExpiringSoon(now));
        await using var web = await WebFixture.Create(); await web.Certificates.CreatePrivate((await web.Settings.Read()).Version, "https://localhost:8787", 8787); await web.Login();
        foreach (var (days, warned) in new[] { (31.0, false), (30.0, true) })
        {
            var info = (await web.Certificates.ReadInfo())! with { ValidUntil = DateTimeOffset.UtcNow.AddDays(days).AddMinutes(-1) };
            web.Database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, "UPDATE public_assets SET value=$value WHERE name='tls_info'",
                ("$value", JsonSerializer.Serialize(info, ConfigurationFiles.Json))));
            foreach (var path in new[] { "/", "/Https", "/Connection" })
            {
                using var page = await web.Http.GetAsync(web.Url + path);
                var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
                Check(page.IsSuccessStatusCode && html.Contains("证书将在") == warned, $"{path} at {days} days");
            }
        }
    }

    /// <summary>P-075：/healthz 字节不变；本机管理端口的 /readyz 在没配上游、库打不开时分别如实报未就绪（503），配好后就绪；对外中转端口没有这个入口。</summary>
    public static async Task ReadinessIsHonestAndLocal()
    {
        await using var mock = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        using (var health = await web.Http.GetAsync(web.Url + "/healthz")) Check(await health.Content.ReadAsByteArrayAsync() is var bytes && Encoding.UTF8.GetString(bytes) == "{\"ok\":true}");
        var (status, body) = await Get(web, "/readyz");
        Check(status == HttpStatusCode.ServiceUnavailable && body.Contains("\"name\":\"upstream\",\"ready\":false") && body.Contains("还没有可用的上游") && body.Contains("\"name\":\"database\",\"ready\":true"), body);
        await mock.Activate(web.Upstreams);
        (status, body) = await Get(web, "/readyz");
        Check(status == HttpStatusCode.OK && body.Contains("\"status\":\"ready\""), body);
        var database = Path.Combine(web.Home.Path, ".state", "relay.db");
        // 临时把库挪开模拟「库打不开」（连接不池化，请求之间没有打开的连接）；下面的收尾块一定挪回来。
        File.Move(database, database + ".moved");
        try
        {
            (status, body) = await Get(web, "/readyz");
            Check(status == HttpStatusCode.ServiceUnavailable && body.Contains("\"name\":\"database\",\"ready\":false") && body.Contains("数据库打不开"), body);
        }
        // 无论核对成败都把库放回去，夹具释放时照常关闭。
        finally { File.Move(database + ".moved", database); }
        await using var relay = web.Relay.Build(null, 0); await relay.StartAsync();
        using var outside = await web.Http.GetAsync(relay.Urls.Single() + "/readyz");
        var text = await outside.Content.ReadAsStringAsync();
        Check(outside.StatusCode != HttpStatusCode.OK && !text.Contains("database"), "The public port must not expose readiness: " + text);
    }

    /// <summary>P-015：新建实例的自动切换默认关；已有库里存的「开」重开、升级后仍是开。</summary>
    public static async Task FailoverDefaultsOffForNewInstances()
    {
        using (var home = new TestHome())
        {
            var first = new TestProcess(home.Path); Check(!(await first.Upstreams.ReadOverview()).Failover, "A new instance starts with failover off");
            await first.Upstreams.SetFailover(true);
            Check((await new TestProcess(home.Path).Upstreams.ReadOverview()).Failover, "A stored switch survives reopening");
        }
        using var legacy = DatabaseUpgradeTests.Legacy("v8");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(legacy.Path, ".state", "relay.db"), Pooling = false }.ToString()))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE upstream SET failover=1"; command.ExecuteNonQuery(); }
        Check((await new TestProcess(legacy.Path).Upstreams.ReadOverview()).Failover, "An upgraded database keeps its stored switch");
    }

    /// <summary>P-010：一家已保存上游的配置读不懂时，上游页照常打开、把它读成「配置已损坏」，备用顺序跳过它，日志留一行；不能拿它获取模型。</summary>
    public static async Task DamagedCandidateDoesNotBreakThePage()
    {
        await using var mock = await UpstreamFixture.Create(); await using var web = await WebFixture.Create(); await web.Login();
        await mock.Activate(web.Upstreams);
        var id = await web.Upstreams.SaveCandidate((await web.Upstreams.ReadActive()).ActiveVersion, mock.Options(), "fixture-damaged-secret");
        web.Database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, "UPDATE upstream_candidates SET options_json='{broken', state='passed' WHERE id=$id", ("$id", id)));
        var listed = await web.Upstreams.ListCandidates();
        Check(listed.Single(item => item.Id == id).Damaged && (await web.Upstreams.ReadStandby()).All(item => item.UpstreamCandidateId != id));
        using var page = await web.Http.GetAsync(web.Url + "/Upstream");
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        Check(page.IsSuccessStatusCode && html.Contains("配置已损坏：读不懂这一家存的配置"), "The page must open and name the damaged upstream");
        var refused = await Refusal(() => web.Upstreams.DiscoverModels(id, CancellationToken.None));
        Check(refused.Contains("配置已损坏"), refused);
        Check((await LogLines(web.Home.Path, 1, line => line.Contains("\"stage\":\"upstream-config:"))).Count >= 1);
    }

    /// <summary>P-011：导入时某一家遇到意外的数据库故障，记进它自己的结果，其余照导，日志留一行。</summary>
    public static async Task ImportRecordsEachFailure()
    {
        await using var mock = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        var version = (await web.Upstreams.ReadActive()).ActiveVersion;
        var good = await web.Upstreams.SaveCandidate(version, mock.Options(), "fixture-import-good", "好的一家");
        var bad = await web.Upstreams.SaveCandidate(version, mock.Options(), "fixture-import-bad", "坏的一家");
        var export = await web.Upstreams.Export([good, bad], "fixture-export-password");
        web.Database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction,
            "CREATE TRIGGER fixture_import BEFORE INSERT ON upstream_candidates WHEN NEW.name='坏的一家' BEGIN SELECT RAISE(ABORT,'fixture-import-failure'); END"));
        var outcomes = await web.Upstreams.Import(version, export.File, "fixture-export-password", CancellationToken.None);
        Check(outcomes.Count == 2 && outcomes.Single(item => item.Name == "好的一家").Error is null
            && outcomes.Single(item => item.Name == "坏的一家").Error?.Contains("意外错误") == true, string.Join(" / ", outcomes.Select(item => item.Name + ":" + item.Error)));
        Check((await LogLines(web.Home.Path, 1, line => line.Contains("\"stage\":\"upstream-import\""))).Count == 1);
        NoSecrets(web.Home.Path, "fixture-import-failure", "fixture-import-good", "fixture-import-bad");
    }

    /// <summary>P-013：每小时清理遇到没有预料到的异常，计入失败、记一行日志，循环继续，宿主不停。</summary>
    public static async Task CleanupLoopSurvivesFaults()
    {
        using var home = new TestHome(); var clock = new ManualClock(); var failures = 0; var cleanups = 0;
        object? Failed() { Interlocked.Increment(ref failures); return null; }
        var usage = FakeProxy<IUsageService>.Create((method, _) => method.Name switch
        {
            "get_Clock" => clock,
            nameof(IUsageService.Recover) => Task.CompletedTask,
            nameof(IUsageService.Cleanup) => Interlocked.Increment(ref cleanups) == 1 ? Task.FromResult(0) : Task.FromException<int>(new NotSupportedException("fixture-cleanup-fault")),
            nameof(IUsageService.MaintenanceFailure) => Failed(),
            _ => null,
        });
        var serving = FakeProxy<IServingLock>.Create((_, _) => new MemoryStream());
        var identity = FakeProxy<ISiteIdentityService>.Create((method, _) => method.Name == nameof(ISiteIdentityService.Prepare) ? Task.CompletedTask : null);
        using var loggers = LoggerFactory.Create(logging => logging.AddRelayFileLog(home.Path));
        using var maintenance = new UsageMaintenance(usage, serving, identity, loggers.CreateLogger<UsageMaintenance>());
        await maintenance.StartAsync(CancellationToken.None);
        // 后台循环在线程池上启动：等它建好周期计时器再触发，否则第一下落空。
        for (var waited = 0; !clock.Ready && waited < 100; waited++) await Task.Delay(50);
        for (var round = 1; round <= 2; round++)
        {
            clock.Fire();
            for (var waited = 0; Volatile.Read(ref failures) < round && waited < 100; waited++) await Task.Delay(50);
        }
        Check(failures == 2 && cleanups == 3 && maintenance.ExecuteTask is { IsCompleted: false }, $"failures={failures}, cleanups={cleanups}, task={maintenance.ExecuteTask?.Status}: {maintenance.ExecuteTask?.Exception}");
        await maintenance.StopAsync(CancellationToken.None);
        Check((await LogLines(home.Path, 2, line => line.Contains("\"stage\":\"usage-cleanup\""))).Count == 2);
        NoSecrets(home.Path, "fixture-cleanup-fault");
    }

    /// <summary>P-014：只有 Claude Code 检测没走完才读「Claude Code 检测未完成」；别的本地拒绝读出它自己的原因，不归到 Claude Code。</summary>
    public static async Task ProbeAttributesOnlyClaudeCodeFailures()
    {
        Exception fault = new InvalidOperationException("本地地址规则拒绝了这次请求。");
        var fake = FakeProxy<IClaudeCodeProbe>.Create((method, _) => method.Name switch
        {
            "get_IsAvailable" => true,
            nameof(IClaudeCodeProbe.Run) => Task.FromException<ProbeReply>(fault),
            _ => null,
        });
        await using var mock = await UpstreamFixture.Create(); await using var web = await WebFixture.Create(overrides: services => services.AddSingleton(fake));
        var options = mock.Options() with { Diagnostic = DiagnosticMethod.ClaudeCode };
        var other = await web.Upstreams.TestCandidate(await web.Upstreams.SaveCandidate((await web.Upstreams.ReadActive()).ActiveVersion, options, "fixture-probe-a"), true, CancellationToken.None);
        Check(other.Message.Contains("本地地址规则拒绝了这次请求") && !other.Message.Contains("Claude Code 检测未完成"), other.Message);
        fault = new ClaudeCodeProbeIncompleteException("Claude Code 未发送检测请求。");
        var native = await web.Upstreams.TestCandidate(await web.Upstreams.SaveCandidate((await web.Upstreams.ReadActive()).ActiveVersion, options, "fixture-probe-b"), true, CancellationToken.None);
        Check(native.Message.StartsWith("Claude Code 检测未完成", StringComparison.Ordinal), native.Message);
    }

    /// <summary>P-095：中转错误体带请求编号，用量里查得到同一个编号；个人用量 JSON 不含上游错误原话（管理员用量页照常读得到）。</summary>
    public static async Task ErrorsCarryRequestIdAndPersonalUsageHidesQuotes()
    {
        await using var mock = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        await mock.Activate(web.Upstreams); var person = await web.Identities.Create("客厅");
        using (var request = new HttpRequestMessage(HttpMethod.Post, web.Url + "/v1/messages") { Content = new StringContent("not json", Encoding.UTF8, "text/plain") })
        {
            request.Headers.Authorization = new("Bearer", person.Token);
            using var response = await web.Http.SendAsync(request);
            var requestId = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("request_id").GetString();
            Check(response.StatusCode == HttpStatusCode.UnsupportedMediaType && (await web.RelayUsage.Query(new())).Rows.Any(row => row.RequestId == requestId), "The error body must carry the usage request id");
        }
        mock.Handler = context => { context.Response.StatusCode = 500; return context.Response.WriteAsJsonAsync(new { type = "error", error = new { type = "api_error", message = "fixture-attempt-words" } }); };
        using (var failed = await UpstreamFixture.RelayRequest(web, person.Token)) Check(failed.StatusCode == HttpStatusCode.InternalServerError);
        using var mine = new HttpRequestMessage(HttpMethod.Get, web.Url + "/my-usage/data"); mine.Headers.Authorization = new("Bearer", person.Token);
        using var personal = await web.Http.SendAsync(mine);
        var json = await personal.Content.ReadAsStringAsync();
        Check(personal.IsSuccessStatusCode && !json.Contains("fixture-attempt-words"), "Personal usage must not quote the upstream: " + json);
        await web.Login();
        using var admin = await web.Http.GetAsync(web.Url + "/Usage");
        Check(WebUtility.HtmlDecode(await admin.Content.ReadAsStringAsync()).Contains("fixture-attempt-words"), "The administrator usage page still reads the quote");
    }

    /// <summary>不经夹具的等待助手提交一张表单，原样返回页面（检测还在进行时也马上返回）。</summary>
    private static async Task<string> RawPost(WebFixture web, string path, Dictionary<string, string> values)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, web.Url + path) { Content = new FormUrlEncodedContent(values) };
        request.Headers.TryAddWithoutValidation("Origin", web.Url);
        using var response = await web.Http.SendAsync(request);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    /// <summary>GET 一个路径，返回状态码与正文。</summary>
    private static async Task<(HttpStatusCode Status, string Body)> Get(WebFixture web, string path)
    {
        using var response = await web.Http.GetAsync(web.Url + path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>等错误日志里出现至少 <paramref name="count"/> 行满足条件的记录（最多 10 秒），返回这些行。</summary>
    private static async Task<IReadOnlyList<string>> LogLines(string home, int count, Func<string, bool> wanted)
    {
        var path = RelayLogFiles.Errors(home).Path; string[] found = [];
        for (var waited = 0; waited < 200; waited++)
        {
            found = ReadShared(path).Split('\n').Where(line => line.Length > 0 && wanted(line)).ToArray();
            if (found.Length >= count) break;
            await Task.Delay(50);
        }
        Check(found.Length >= count, $"Expected {count} log lines in {path}");
        return found;
    }

    /// <summary>数据目录 logs 下的全部日志里不含这些字样（令牌、凭据、异常消息）。</summary>
    private static void NoSecrets(string home, params string[] secrets)
    {
        var folder = RelayLogFiles.Folder(home);
        var text = Directory.Exists(folder) ? string.Concat(Directory.EnumerateFiles(folder).Select(ReadShared)) : "";
        foreach (var secret in secrets.Append(FakeToken)) Check(!text.Contains(secret, StringComparison.Ordinal), "The log contains " + secret);
    }

    /// <summary>与写日志的一方共享读取（写方每次追加都单独打开文件）。</summary>
    private static string ReadShared(string path)
    {
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>执行并返回它抛出的业务拒绝读句；没有拒绝时让用例失败。</summary>
    private static async Task<string> Refusal(Func<Task> action)
    {
        try { await action(); }
        // 这里要的就是业务拒绝：收下读句交给调用方核对。
        catch (InvalidOperationException error) { return error.Message; }
        Check(false, "Expected a refusal"); return "";
    }

    /// <summary>仓库根目录：从测试程序目录往上找解决方案文件。</summary>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "HouseholdRelay.sln"))) return directory.FullName;
        throw new InvalidOperationException("找不到仓库根目录。");
    }

    /// <summary>写什么都抛 IOException 的控制台：模拟服务窗口已关、输出写不出去。</summary>
    private sealed class BrokenWriter : TextWriter
    {
        /// <summary>编码。</summary>
        public override Encoding Encoding => Encoding.UTF8;
        /// <summary>写一个字符就失败。</summary>
        public override void Write(char value) => throw new IOException("console closed");
        /// <summary>写一行就失败。</summary>
        public override void WriteLine(string? value) => throw new IOException("console closed");
    }

    /// <summary>手动触发的计时器时钟：<see cref="Fire"/> 一次就让周期计时器走一格。</summary>
    private sealed class ManualClock : TimeProvider
    {
        private (TimerCallback Callback, object? State)? timer;

        /// <summary>记下计时器回调，不自己走。</summary>
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) { timer = (callback, state); return new ManualTimer(); }

        /// <summary>触发一次。</summary>
        public void Fire() { if (timer is { } registered) registered.Callback(registered.State); }

        /// <summary>已经有计时器登记了。</summary>
        public bool Ready => timer is not null;

        /// <summary>什么都不做的计时器句柄。</summary>
        private sealed class ManualTimer : ITimer
        {
            /// <summary>不改。</summary>
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            /// <summary>不释放什么。</summary>
            public void Dispose() { }
            /// <summary>不释放什么。</summary>
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
