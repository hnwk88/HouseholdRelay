using System.Net.Http.Json;
using HouseholdRelay;
using static Tests;
using static UsageFixture;

static class UsageRestartTests
{
    public static async Task GracefulRestart()
    {
        await using var upstream = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        var user = web.Identities.Create("正常重启"); await upstream.Activate(new(web.Database));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        upstream.Handler = async context => { entered.TrySetResult(); await release.Task; await Json(context, new { input_tokens = 12, output_tokens = 9 }); };
        var call = Call(web, user.Token); await entered.Task;
        var stopping = web.App.StopAsync(); release.TrySetResult();
        Check((int)await call == 200); await stopping;
        var original = web.Relay.Usage!.Query(new()).Rows.Single();
        Check(original.Outcome == "succeeded" && original.Completeness == "complete");
        var database = new RelayDatabase(web.Home.Path); var identities = new IdentityService(database);
        using var relay = new RelayServer(new ConfigurationService(database).Read().Value, null, clients: identities.Clients, upstreams: new UpstreamService(database));
        await using var app = ManagementWeb.Build(database, identities, relay, 0); await app.StartAsync();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, app.Urls.Single() + "/my-usage/data"); request.Headers.Authorization = new("Bearer", user.Token);
            using var response = await web.Http.SendAsync(request); Check(response.IsSuccessStatusCode);
            var report = (await response.Content.ReadFromJsonAsync<UsageReport>())!;
            Check(report.Total == 1 && report.Rows.Single() == original, "Graceful shutdown must finish persistence before recovery runs");
        }
        finally { await app.StopAsync(); }
    }
}
