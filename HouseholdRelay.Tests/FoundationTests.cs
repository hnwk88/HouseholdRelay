using System.Net;
using System.Net.Http.Headers;
using System.Text;
using HouseholdRelay;
using Microsoft.AspNetCore.Http;
using static Tests;

static partial class FoundationTests
{
    public static Task Persistence()
    {
        using var home = new TestHome(); var database = new RelayDatabase(home.Path);
        var settings = new ConfigurationService(database); var identities = new IdentityService(database); var administrators = new AdministratorService(database);
        Check(identities.List().Count == 0 && !settings.HasCredential && !settings.HasCertificate);
        settings.Read().Value.Validate();
        using (var connection = database.Open())
        {
            Check(Convert.ToInt32(RelayDatabase.Scalar(connection, null, "PRAGMA user_version")) == 1);
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
        try { settings.Save(snapshot.Version, new RelayConfiguration()); } catch (InvalidOperationException) { staleRejected = true; }
        Check(staleRejected, "A stale form must not overwrite a committed configuration");
        var restarted = new RelayDatabase(home.Path); var restored = new IdentityService(restarted);
        Check(restored.Clients.Authenticate(rotated.Token)?.Id == a.UserId);
        Check(restored.List().Single(user => user.Id == a.UserId).DisplayName == "甲的显示名");
        Check(new ConfigurationService(restarted).Read().Value.ListenPort == 9001);
        Check(new AdministratorService(restarted).IsSessionValid(session)); administrators.Logout(session);
        Check(!new AdministratorService(restarted).IsSessionValid(session));
        var allBytes = Encoding.UTF8.GetString(File.ReadAllBytes(database.Path));
        Check(!allBytes.Contains(a.Token) && !allBytes.Contains(rotated.Token) && !allBytes.Contains(WebFixture.Password));
        return Task.CompletedTask;
    }

    public static Task Migration()
    {
        using var home = new TestHome(); var legacy = home.WriteLegacy();
        var database = new RelayDatabase(home.Path); var identities = new IdentityService(database); var settings = new ConfigurationService(database);
        var first = identities.Clients.Authenticate("legacy-study-token"); Check(first is not null && first.UserId is not null);
        Check(identities.Clients.Authenticate("legacy-office-token") is null);
        Check(settings.Read().Value.DeviceId == legacy.DeviceId && settings.Read().Value.UpstreamUserAgent == legacy.UpstreamUserAgent);
        Check(settings.LoadCredential() == "fixture-legacy-upstream-secret");
        using (var certificate = Management.LoadServerCertificate(settings.LoadCertificate()!)) Check(certificate.HasPrivateKey);
        var backup = Directory.GetDirectories(System.IO.Path.Combine(database.State, "legacy-backups")).Single();
        foreach (var name in new[] { "server.json", "account.dpapi", "tls.dpapi", "relay-ca.pem" })
            Check(File.ReadAllBytes(System.IO.Path.Combine(backup, name)).SequenceEqual(File.ReadAllBytes(System.IO.Path.Combine(database.State, name))));
        File.WriteAllText(System.IO.Path.Combine(database.State, "server.json"), "untrusted obsolete JSON");
        var reopened = new IdentityService(new RelayDatabase(home.Path));
        Check(reopened.Clients.Authenticate("legacy-study-token")?.Id == first!.Id);
        Check(reopened.List().Count == 2, "Repeated startup must not reimport");
        return Task.CompletedTask;
    }

    public static Task Recovery()
    {
        using var home = new TestHome(); home.WriteLegacy();
        var source = System.IO.Path.Combine(home.Path, ".state", "server.json"); var before = File.ReadAllBytes(source);
        var failed = false;
        try { _ = new RelayDatabase(home.Path, () => throw new IOException("injected before migration commit")); }
        catch (IOException) { failed = true; }
        Check(failed && File.ReadAllBytes(source).SequenceEqual(before));
        var database = new RelayDatabase(home.Path);
        Check(new IdentityService(database).List().Count == 2, "Retry after rollback must import exactly once");
        var administrators = new AdministratorService(database); administrators.Initialize(administrators.BootstrapCode()!, WebFixture.Password);
        File.Delete(database.Path);
        var lostRejected = false;
        try { _ = new RelayDatabase(home.Path); } catch (InvalidOperationException) { lostRejected = true; }
        Check(lostRejected && !File.Exists(database.Path), "Missing database must not open setup again");
        return Task.CompletedTask;
    }

    public static async Task ConcurrentWrites()
    {
        using var home = new TestHome(); var db = new RelayDatabase(home.Path);
        var service = new IdentityService(db); var user = service.Create("甲");
        var other = new IdentityService(new RelayDatabase(home.Path));
        // 两个服务独立连接同时写不同列，事务不能回写整份过期名单。
        await Task.WhenAll(Task.Run(() => service.Rename(user.UserId, "改名后的甲")), Task.Run(() => other.SetEnabled(user.UserId, false)));
        Check(service.List().Single().DisplayName == "改名后的甲" && !service.List().Single().Enabled);
        Check(service.Clients.Authenticate(user.Token) is null, "Other service commits must revoke immediately");
        var races = Enumerable.Range(0, 10).Select(index => Task.Run(() =>
        {
            try { new IdentityService(db).Create("使用者" + index); return true; } catch (InvalidOperationException) { return false; }
        }));
        var results = await Task.WhenAll(races); Check(results.Count(success => success) == 7 && service.List().Count == 8);
        for (var index = 0; index < 10; index++) service.Rotate(user.UserId);
        Check(service.List().Count == 8 && service.List().Single(item => item.Id == user.UserId).TokenVersion == 11);
    }

    public static Task PermissionFailure()
    {
        using var home = new TestHome(); var database = new RelayDatabase(home.Path); var identities = new IdentityService(database);
        var issued = identities.Create("故障验证用户");
        database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction,
            "CREATE TRIGGER reject_token_insert BEFORE INSERT ON tokens BEGIN SELECT RAISE(ABORT,'fixture failure'); END"));
        var failed = false;
        try { identities.Rotate(issued.UserId); } catch (Microsoft.Data.Sqlite.SqliteException) { failed = true; }
        Check(failed && identities.Clients.Authenticate(issued.Token)?.Id == issued.UserId);
        Check(identities.List().Single().TokenVersion == 1, "Failed rotation must roll back old-token revocation");
        database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, "DROP TRIGGER reject_token_insert"));
        var replacement = identities.Rotate(issued.UserId);
        Check(identities.Clients.Authenticate(issued.Token) is null && identities.Clients.Authenticate(replacement.Token)?.Id == issued.UserId);
        return Task.CompletedTask;
    }

    public static async Task Revocation()
    {
        using var home = new TestHome(); var db = new RelayDatabase(home.Path); var identities = new IdentityService(db);
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
        var external = new IdentityService(new RelayDatabase(home.Path)); var rotated = external.Rotate(a.UserId);
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
