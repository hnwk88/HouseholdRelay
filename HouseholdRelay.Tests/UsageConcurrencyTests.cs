using System.Diagnostics;
using HouseholdRelay;
using Microsoft.AspNetCore.Http;
using static Tests;
using static UsageFixture;

static class UsageConcurrencyTests
{
    public static async Task WindowQueriesAndLoad()
    {
        await using var upstream = await UpstreamFixture.Create();
        await using var web = await WebFixture.Create(config => { config.MaxConcurrent = 8; config.MaxPerClient = 8; config.MaxQueued = 64; });
        var a = web.Identities.Create("并发甲"); var b = web.Identities.Create("并发乙"); await upstream.Activate(new(web.Database));
        var client = web.Identities.Clients.Authenticate(a.Token)!;
        // 一次事务批量造历史数据，不能用逐条提交掩盖清理和索引性能。
        web.Database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, """
            WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM rows WHERE n<10000)
            INSERT INTO usage_requests(request_id,user_id,token_id,request_started_at,kind,outcome,completeness,input_tokens,output_tokens)
            SELECT 'old-'||n,$user,$token,$time,'inference','succeeded','complete',999,999 FROM rows
            """, ("$user", client.Id), ("$token", client.TokenId), ("$time", DateTimeOffset.UtcNow.AddDays(-41).UtcTicks)));
        upstream.Handler = context => Json(context, new { input_tokens = 2, output_tokens = 3 });
        var timer = Stopwatch.StartNew(); var before = DateTimeOffset.UtcNow;
        var requests = Enumerable.Range(0, 48).Select(index => Call(web, index % 2 == 0 ? a.Token : b.Token)).ToArray();
        var management = Task.Run(async () =>
        {
            for (var index = 0; index < 10; index++)
            { web.Identities.Rename(a.UserId, "并发甲" + index); _ = web.Relay.Usage!.Query(new(a.UserId)); await Task.Delay(10); }
        });
        var statuses = await Task.WhenAll(requests); await management;
        Check(statuses.All(status => (int)status == 200), string.Join(", ", statuses.GroupBy(status => status).Select(group => group.Key + ":" + group.Count())));
        var report = await Settled(web, 48); var elapsed = timer.Elapsed;
        Check(elapsed < TimeSpan.FromSeconds(20), "Small deployment load exceeded the bounded test budget");
        Check(report.Total == 48 && report.Rows.Count == 25 && report.Summary[0].Tokens == new UsageTokens(96, 144));
        var second = web.Relay.Usage!.Query(new(Page: 2)); Check(second.Rows.Count == 23 && !second.Rows.Select(row => row.RequestId).Intersect(report.Rows.Select(row => row.RequestId)).Any());
        var filter = new UsageFilter(a.UserId, before, DateTimeOffset.UtcNow.AddSeconds(1), "inference", "fixture-model", "succeeded");
        Check(web.Relay.Usage.Query(filter).Total == 24 && web.Relay.Usage.Query(filter with { Model = "absent" }).Total == 0);
        Check(web.Relay.Usage.Query(filter with { Until = before }).Total == 0);
        using (var connection = web.Database.Open())
        {
            using var command = RelayDatabase.Command(connection, null, "EXPLAIN QUERY PLAN SELECT request_id FROM usage_requests WHERE user_id=$user AND request_started_at >= $cutoff ORDER BY request_started_at DESC,request_id DESC LIMIT 25", ("$user", a.UserId), ("$cutoff", DateTimeOffset.UtcNow.AddDays(-40).UtcTicks));
            using var reader = command.ExecuteReader(); var plan = new List<string>(); while (reader.Read()) plan.Add(reader.GetString(3));
            Check(plan.Any(line => line.Contains("SEARCH") && line.Contains("usage_user_time")), "Personal window queries must use their covering index");
        }
        var removed = await web.Relay.Usage.Cleanup(); Check(removed == 10000 && web.Relay.Usage.Query(new()).Total == 48);
        Check(upstream.Calls.Count == 50 && web.Relay.Usage.PersistenceFailures == 0);
        Console.WriteLine($"USAGE LOAD: 48 concurrent requests + 10 management writes/queries, {elapsed.TotalMilliseconds:F0} ms; deleted {removed} old rows in batches; index=usage_user_time");
    }

    public static async Task QueueAndDispatchFailures()
    {
        await using var upstream = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        var user = web.Identities.Create("排队"); await upstream.Activate(new(web.Database));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        upstream.Handler = async context => { entered.TrySetResult(); await release.Task.WaitAsync(context.RequestAborted); await Json(context, new { input_tokens = 5, output_tokens = 6 }); };
        var first = Call(web, user.Token); await entered.Task;
        var second = Call(web, user.Token); await Until(() => web.Relay.Admission.Queued == 1);
        var rotated = web.Identities.Rotate(user.UserId); release.TrySetResult();
        Check((int)await first == 200 && (int)await second == 401);
        var report = await Settled(web, 2);
        Check(report.Rows.All(row => row.UserId == user.UserId) && report.Rows.Count(row => !row.Dispatched && row.Completeness == "not_applicable") == 1);
        var calls = upstream.Calls.Count;
        web.Database.Write((connection, transaction) => RelayDatabase.Execute(connection, transaction, """
            CREATE TRIGGER reject_usage_dispatch BEFORE UPDATE ON usage_requests WHEN NEW.dispatched=1
            BEGIN SELECT RAISE(ABORT,'fixture-dispatch-failure'); END
            """));
        Check((int)await Call(web, rotated.Token) == 503);
        var row = (await Settled(web, 3)).Rows[0]; Check(!row.Dispatched && row.Outcome == "failed" && row.Completeness == "not_applicable");
        Check(upstream.Calls.Count == calls);
    }
}
