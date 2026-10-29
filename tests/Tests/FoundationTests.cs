using System.Net;
using System.Net.Http.Headers;
using System.Text;
using HouseholdRelay;
using Microsoft.AspNetCore.Http;
using static Tests.TestSuite;

namespace Tests;

static partial class FoundationTests
{
    /// <summary>
    /// 验证数据库身份、设置和会话的持久化：新库为空，且启用 WAL、完全同步和外键；管理员初始化后引导码失效；
    /// 停用成员轮换后仍保持停用；过期版本的配置保存被拒绝；重启后轮换令牌、改名、端口和登录会话都还在，
    /// 注销后重启进程也认不出该会话；数据库文件中不含明文令牌和管理员密码。
    /// </summary>
    public static Task Persistence()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path); var database = process.Database;
        var settings = process.Settings; var identities = process.Identities; var administrators = process.Administrators;
        Check(identities.List().Count == 0 && !settings.HasCredential && !settings.HasCertificate);
        settings.Read().Value.Validate();
        using (var connection = database.Open())
        {
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")) == 4);
            Check((string?)RelayDatabase.Scalar(connection, null, "PRAGMA journal_mode") == "wal");
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA synchronous")) == 2);
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA foreign_keys")) == 1);
        }
        administrators.Initialize(administrators.BootstrapCode()!, WebFixture.Password);
        Check(administrators.BootstrapCode() is null);
        var session = administrators.Login(WebFixture.Password)!; Check(administrators.IsSessionValid(session));
        var a = identities.Create("甲"); var b = identities.Create("乙");
        identities.SetEnabled(b.UserId, false); var disabled = identities.Rotate(b.UserId);
        Check(identities.Clients.Authenticate(disabled.Token) is null, "Rotation must preserve disabled state");
        identities.Rename(a.UserId, "甲的显示名"); var rotated = identities.Rotate(a.UserId);
        Check(identities.Clients.Authenticate(a.Token) is null);
        var snapshot = settings.Read(); snapshot.Value.ListenPort = 9001; settings.Save(snapshot.Version, snapshot.Value);
        var staleRejected = false;
        try { settings.Save(snapshot.Version, new RelayConfiguration()); } catch (InvalidOperationException) { /* 用已提交过的旧版本号保存必须被拒绝；捕获到即记为拒绝成功，未抛出则下一行断言判失败。 */ staleRejected = true; }
        Check(staleRejected, "A stale form must not overwrite a committed configuration");
        var restarted = new TestProcess(home.Path); var restored = restarted.Identities;
        Check(restored.Clients.Authenticate(rotated.Token)?.Id == a.UserId);
        Check(restored.List().Single(user => user.Id == a.UserId).DisplayName == "甲的显示名");
        Check(restarted.Settings.Read().Value.ListenPort == 9001);
        Check(restarted.Administrators.IsSessionValid(session)); administrators.Logout(session);
        Check(!restarted.Administrators.IsSessionValid(session));
        var allBytes = Encoding.UTF8.GetString(File.ReadAllBytes(database.Path));
        Check(!allBytes.Contains(a.Token) && !allBytes.Contains(rotated.Token) && !allBytes.Contains(WebFixture.Password));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 验证旧版安装迁移：启用的旧令牌可登录并获得成员编号，停用的旧令牌仍被拒绝；设备号、User-Agent、上游凭据和带私钥的证书都被保留；
    /// 旧文件原样备份到 legacy-backups；之后即使旧 server.json 被篡改，再次启动也不会重复导入（成员仍为 2 人、编号不变）。
    /// </summary>
    public static Task Migration()
    {
        using var home = new TestHome(); var legacy = home.WriteLegacy();
        var process = new TestProcess(home.Path); var database = process.Database; var identities = process.Identities; var settings = process.Settings;
        var first = identities.Clients.Authenticate("legacy-study-token"); Check(first is not null && first.UserId is not null);
        Check(identities.Clients.Authenticate("legacy-office-token") is null);
        Check(settings.Read().Value.DeviceId == legacy.DeviceId && settings.Read().Value.UpstreamUserAgent == legacy.UpstreamUserAgent);
        Check(process.ActiveCredential() == "fixture-legacy-upstream-secret");
        using (var certificate = TestProcess.CertificateFactory.LoadServerCertificate(settings.LoadCertificate()!)) Check(certificate.HasPrivateKey);
        var backup = Directory.GetDirectories(System.IO.Path.Combine(database.State, "legacy-backups")).Single();
        foreach (var name in new[] { "server.json", "account.dpapi", "tls.dpapi", "relay-ca.pem" })
            Check(File.ReadAllBytes(System.IO.Path.Combine(backup, name)).SequenceEqual(File.ReadAllBytes(System.IO.Path.Combine(database.State, name))));
        File.WriteAllText(System.IO.Path.Combine(database.State, "server.json"), "untrusted obsolete JSON");
        var reopened = new TestProcess(home.Path).Identities;
        Check(reopened.Clients.Authenticate("legacy-study-token")?.Id == first!.Id);
        Check(reopened.List().Count == 2, "Repeated startup must not reimport");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 验证迁移回滚与数据库丢失时失败即关闭：在迁移提交前注入故障，启动失败且旧文件字节不变；重试后恰好导入一次；
    /// 管理员初始化后删除数据库，再次启动必须报错且不新建数据库，防止任何人借机重新进入首次设置。
    /// </summary>
    public static Task Recovery()
    {
        using var home = new TestHome(); home.WriteLegacy();
        var source = System.IO.Path.Combine(home.Path, ".state", "server.json"); var before = File.ReadAllBytes(source);
        var failed = false;
        try { _ = new TestProcess(home.Path, migrationCheckpoint: () => throw new IOException("injected before migration commit")).Database; }
        // 注入的提交前故障预期以 IOException 冒出；捕获后再核对旧文件未被改动，证明迁移整体回滚。
        catch (IOException) { failed = true; }
        Check(failed && File.ReadAllBytes(source).SequenceEqual(before));
        var process = new TestProcess(home.Path); var database = process.Database;
        Check(process.Identities.List().Count == 2, "Retry after rollback must import exactly once");
        var administrators = process.Administrators; administrators.Initialize(administrators.BootstrapCode()!, WebFixture.Password);
        // 故意删除已完成初始化的数据库，模拟数据库文件丢失。
        File.Delete(database.Path);
        var lostRejected = false;
        try { _ = new TestProcess(home.Path).Database; } catch (InvalidOperationException) { /* 已初始化过的安装丢了数据库必须拒绝启动，而不是新建空库重新开放首次设置。 */ lostRejected = true; }
        Check(lostRejected && !File.Exists(database.Path), "Missing database must not open setup again");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 验证并发管理写入：两个独立进程同时改名和停用同一成员，两项修改都生效且停用立即吊销令牌；
    /// 10 个进程并发新建成员时恰好 7 个成功（连同已有成员达到 8 人上限），其余被拒绝；
    /// 连续轮换 10 次后令牌版本为 11 且成员数不变。
    /// </summary>
    public static async Task ConcurrentWrites()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path); var db = process.Database;
        var service = process.Identities; var user = service.Create("甲");
        var other = new TestProcess(home.Path).Identities;
        // 两个服务独立连接同时写不同列，事务不能回写整份过期名单。
        await Task.WhenAll(Task.Run(() => service.Rename(user.UserId, "改名后的甲")), Task.Run(() => other.SetEnabled(user.UserId, false)));
        Check(service.List().Single().DisplayName == "改名后的甲" && !service.List().Single().Enabled);
        Check(service.Clients.Authenticate(user.Token) is null, "Other service commits must revoke immediately");
        var races = Enumerable.Range(0, 10).Select(index => Task.Run(() =>
        {
            try { new TestProcess(home.Path).Identities.Create("使用者" + index); return true; } catch (InvalidOperationException) { /* 超出成员上限的创建会被拒绝，记为失败以便统计成功数。 */ return false; }
        }));
        // 等待全部并发创建结束；成功数必须恰好为 7，说明上限检查和插入在同一事务里，不会被并发突破。
        var results = await Task.WhenAll(races); Check(results.Count(success => success) == 7 && service.List().Count == 8);
        for (var index = 0; index < 10; index++) service.Rotate(user.UserId);
        Check(service.List().Count == 8 && service.List().Single(item => item.Id == user.UserId).TokenVersion == 11);
    }

    /// <summary>
    /// 验证权限写入失败不会发布半截吊销：用触发器让新令牌写入失败，轮换必须报错，旧令牌仍可用且令牌版本仍为 1；
    /// 去掉触发器后轮换成功，旧令牌失效、新令牌指向同一成员。
    /// </summary>
    public static Task PermissionFailure()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path); var database = process.Database; var identities = process.Identities;
        var issued = identities.Create("故障验证用户");
        database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction,
            "CREATE TRIGGER reject_token_insert BEFORE INSERT ON tokens BEGIN SELECT RAISE(ABORT,'fixture failure'); END"));
        var failed = false;
        try { identities.Rotate(issued.UserId); } catch (Microsoft.Data.Sqlite.SqliteException) { /* 触发器让写入中止，轮换必须以 SQLite 异常整体回滚，随后核对旧令牌未被吊销。 */ failed = true; }
        Check(failed && identities.Clients.Authenticate(issued.Token)?.Id == issued.UserId);
        Check(identities.List().Single().TokenVersion == 1, "Failed rotation must roll back old-token revocation");
        database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, "DROP TRIGGER reject_token_insert"));
        var replacement = identities.Rotate(issued.UserId);
        Check(identities.Clients.Authenticate(issued.Token) is null && identities.Clients.Authenticate(replacement.Token)?.Id == issued.UserId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 验证数据库令牌吊销在排队和上传两个边界生效：两个请求占满并发、第三个排队时由另一进程轮换甲的令牌，
    /// 在途请求成功、排队请求返回 401；旧令牌随后被拒、新令牌可用；上传卡在中途时停用乙，上传完成后返回 401 且不发往上游；
    /// 改名不改变令牌对应的成员，防止借新显示名绕过同一人的并发限制。
    /// </summary>
    public static async Task Revocation()
    {
        using var home = new TestHome(); var process = new TestProcess(home.Path); var identities = process.Identities;
        var a = identities.Create("甲"); var b = identities.Create("乙");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.Create(async context =>
        { entered.TrySetResult(); await release.Task.WaitAsync(context.RequestAborted); await context.Response.WriteAsync("{}"); }, clients: identities.Clients);
        async Task<HttpResponseMessage> Send(string token)
        {
            using var request = fixture.Request(); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await fixture.Http.SendAsync(request);
        }
        var first = Send(a.Token); await entered.Task;
        var second = Send(b.Token); await Until(() => fixture.Relay.Admission.Active == 2);
        var queued = Send(a.Token); await Until(() => fixture.Relay.Admission.Queued == 1);
        var external = new TestProcess(home.Path).Identities; var rotated = external.Rotate(a.UserId);
        release.TrySetResult();
        using var r1 = await first; using var r2 = await second; using var rq = await queued;
        Check(r1.IsSuccessStatusCode && r2.IsSuccessStatusCode && rq.StatusCode == HttpStatusCode.Unauthorized && fixture.Calls.Count == 2);
        using var old = await Send(a.Token); Check(old.StatusCode == HttpStatusCode.Unauthorized);
        using var fresh = await Send(rotated.Token); Check(fresh.IsSuccessStatusCode);
        using var upload = new PausedUpload(); using var uploadRequest = fixture.Request();
        uploadRequest.Content = upload; uploadRequest.Headers.Authorization = new("Bearer", b.Token);
        var response = fixture.Http.SendAsync(uploadRequest); await upload.Started.Task;
        await Until(() => fixture.Relay.Admission.Active == 1);
        external.SetEnabled(b.UserId, false); upload.Continue.TrySetResult();
        using var rejected = await response; Check(rejected.StatusCode == HttpStatusCode.Unauthorized && fixture.Calls.Count == 3);
        // 改名后仍共享同一人的并发槽；不能通过新显示名绕过排队。
        Check(identities.Clients.Authenticate(rotated.Token)?.Id == a.UserId);
        external.Rename(a.UserId, "重命名的甲");
        Check(identities.Clients.Authenticate(rotated.Token)?.Id == a.UserId);
    }
}
