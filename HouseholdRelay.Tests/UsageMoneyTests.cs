using System.Net;
using HouseholdRelay;
using Microsoft.AspNetCore.Http;
using static Tests;
using static UsageFixture;

static class UsageMoneyTests
{
    public static async Task ReportedCosts()
    {
        await using var upstream = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        var user = web.Identities.Create("费用"); var service = new UpstreamService(web.Database); await upstream.Activate(service);
        var pricing = new UsagePricing(web.Database); var version = service.ReadActive().Version;
        pricing.Save(version, "fixture-model", "USD", 1, 2, 0, 0);
        upstream.Handler = async context =>
        {
            if (context.Request.Query["case"] == "stream")
            {
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync(StartEvent);
                foreach (var amount in new[] { 0.01m, 0.04m, 0.09m })
                    await context.Response.WriteAsync(Event("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = 9, cost_usd = amount } }));
                await context.Response.WriteAsync(StopEvent); return;
            }
            object usage = context.Request.Query["case"].ToString() switch
            {
                "reported" => new { input_tokens = 10, output_tokens = 20, cost = new { amount = "0.123456789", currency = "USD" } },
                "without_tokens" => new { cost_usd = 0.03m },
                "currency" => new { input_tokens = 10, output_tokens = 20, cost = new { amount = 1.25m, currency = "CNY" } },
                "invalid" => new { input_tokens = 10, output_tokens = 20, cost = 123.45m },
                _ => new { input_tokens = 10, output_tokens = 20 }
            };
            await Json(context, usage);
        };
        var expected = new[]
        {
            ("reported", new UsageMoney(0.123456789m, "USD", "reported", "complete")),
            ("without_tokens", new UsageMoney(0.03m, "USD", "reported", "complete")),
            ("currency", new UsageMoney(1.25m, "CNY", "reported", "complete")),
            ("invalid", new UsageMoney(0.00005m, "USD", "estimated", "complete")),
            ("stream", new UsageMoney(0.09m, "USD", "reported", "complete"))
        };
        var count = 0;
        foreach (var (scenario, money) in expected)
        { Check((int)await Call(web, user.Token, scenario) == 200); Check((await Settled(web, ++count)).Rows[0].Money == money, scenario + " money source/amount"); }
        var summary = web.Relay.Usage!.Query(new()).Summary[0];
        Check(summary.Money.Single(item => item.Currency == "USD" && item.Source == "reported").Amount == 0.243456789m);
        Check(summary.Money.Count == 3 && summary.UnknownAmounts == 0);
        Check((await Personal(web, user.Token)).Summary[0].Money.Count == 3);
        await web.Login(); var html = WebUtility.HtmlDecode(await web.Http.GetStringAsync(web.Url + "/Usage"));
        Check(html.Contains("0.243456789 USD") && html.Contains("1.25 CNY") && html.Contains("估算"));
        Check(web.Relay.Usage.Query(new()).Periods.Sum(period => period.Summary.Requests) == 5);
    }

    public static async Task PriceSnapshotsAndTimeZones()
    {
        using var home = new TestHome(); var db = new RelayDatabase(home.Path); var ids = new IdentityService(db); var user = ids.Create("时区"); var client = ids.Clients.Authenticate(user.Token)!;
        var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero); var clock = new UsageClock(now); var store = new UsageStore(db, clock); var prices = new UsagePricing(db);
        prices.Save(1, "priced", "USD", 1, 2, 0, 0);
        var dates = new[] { new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 2, 5, 5, 0, TimeSpan.Zero) };
        for (var index = 0; index < dates.Length; index++)
        {
            clock.Set(dates[index]); var id = "priced-" + index;
            await store.Start(id, client, "inference"); await store.Dispatch(id, 1, "priced");
            if (index == 0) prices.Save(1, "priced", "USD", 100, 200, 0, 0);
            await store.Finish(id, new("succeeded", true, 200, 200, 1, "complete", new(10000, 10000)));
        }
        clock.Set(now); await store.Start("unknown", client, "inference"); await store.Dispatch("unknown", 1, "no-price");
        await store.Finish("unknown", new("succeeded", true, 200, 200, 1, "complete", new(2, 3)));
        var hours = store.Query(new(Granularity: "hour", TimeZone: "America/New_York"));
        Check(hours.Rows.Single(row => row.RequestId == "priced-0").Money.Amount == 0.03m, "Dispatch must snapshot the old price before later changes");
        Check(hours.Summary[0].Money.Single().Amount == 6.03m && hours.Summary[0].UnknownAmounts == 1);
        var repeatedHour = hours.Periods.Where(period => period.Start.Day == 1).ToArray();
        Check(repeatedHour.Length == 2 && repeatedHour.All(period => period.Start.Hour == 1) && repeatedHour.Select(period => period.Start.Offset).Distinct().Count() == 2);
        var days = store.Query(new(Granularity: "day", TimeZone: "America/New_York"));
        Check(days.Periods.Count == 2 && days.Periods.Single(period => period.Start.Day == 1).Summary.Requests == 2);
        var range = store.Query(new(From: dates[0], Until: dates[1], Granularity: "hour", TimeZone: "America/New_York"));
        Check(range.Total == 1 && range.Periods.Single().Summary.Money.Single().Amount == 0.03m);
        var reopened = new UsageStore(new RelayDatabase(home.Path), clock); Check(reopened.Query(new()).Summary[0].Money.Single().Amount == 6.03m);
        prices.Save(1, "partial", "CNY", 2, null, null, null);
        await store.Start("partial", client, "inference"); await store.Dispatch("partial", 1, "partial");
        await store.Finish("partial", new("cancelled", true, 200, 200, 1, "partial", new(1000)));
        Check(store.Query(new(Model: "partial")).Rows.Single().Money == new UsageMoney(0.002m, "CNY", "estimated", "partial"));
        await store.Start("no-usage", client, "inference"); await store.Dispatch("no-usage", 1, "priced");
        await store.Finish("no-usage", new("succeeded", true, 200, 200, 1, "not_provided", new()));
        Check(store.Query(new()).Rows.Single(row => row.RequestId == "no-usage").Money.Amount is null, "Zero-priced cache must not turn entirely unknown consumption into zero cost");
        var untouched = prices.List(1).Count; clock.Advance(TimeSpan.FromDays(41)); await store.Cleanup(); Check(prices.List(1).Count == untouched);
    }

    public static async Task PricingWebSecurity()
    {
        await using var upstream = await UpstreamFixture.Create(); await using var web = await WebFixture.Create();
        var user = web.Identities.Create("个人"); await upstream.Activate(new(web.Database));
        using (var personal = new HttpRequestMessage(HttpMethod.Get, web.Url + "/Pricing"))
        { personal.Headers.Authorization = new("Bearer", user.Token); using var result = await web.Http.SendAsync(personal); Check(result.StatusCode == HttpStatusCode.Redirect); }
        await web.Login();
        var data = new Dictionary<string, string> { ["Version"] = "2", ["ModelName"] = "fixture-model", ["Currency"] = "USD", ["InputPrice"] = "1.2", ["OutputPrice"] = "3.4", ["CacheReadPrice"] = "0", ["CacheWritePrice"] = "0" };
        using (var rejected = await web.Post("/Pricing", data)) Check(rejected.StatusCode == HttpStatusCode.BadRequest);
        data["__RequestVerificationToken"] = await web.Csrf("/Pricing");
        using (var saved = await web.Post("/Pricing", data)) Check(saved.IsSuccessStatusCode);
        var price = new UsagePricing(web.Database).List(2).Single(); Check(price.InputPerMillion == 1.2m && price.OutputPerMillion == 3.4m);
        await upstream.Activate(new(web.Database));
        data["InputPrice"] = "999";
        using (var stale = await web.Post("/Pricing", data))
        { var html = WebUtility.HtmlDecode(await stale.Content.ReadAsStringAsync()); Check(html.Contains("活动上游已改变")); }
        Check(new UsagePricing(web.Database).List(2).Single().InputPerMillion == 1.2m && new UsagePricing(web.Database).List(3).Count == 0);
    }
}
