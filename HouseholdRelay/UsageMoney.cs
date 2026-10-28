using System.Globalization;
using System.Text.Json;
using static HouseholdRelay.RelayDatabase;

namespace HouseholdRelay;

/// <summary>费用来源和完整性独立保存；上游报告金额优先，估算不能冒充实际扣费。</summary>
public sealed record UsageMoney(decimal? Amount = null, string? Currency = null, string Source = "unknown", string Completeness = "unknown");
public sealed record MoneyTotal(string Currency, string Source, decimal Amount, long Requests, long Incomplete);
public sealed record UsagePrice(string Id, long UpstreamVersion, string Model, string Currency,
    decimal? InputPerMillion, decimal? OutputPerMillion, decimal? CacheReadPerMillion, decimal? CacheWritePerMillion);

/// <summary>按活动上游版本和模型保存费率；发送时复制快照，之后改价不改变历史金额。</summary>
public sealed class UsagePricing(RelayDatabase database)
{
    public IReadOnlyList<UsagePrice> List(long version)
    {
        using var connection = database.Open();
        using var command = Command(connection, null, "SELECT json FROM usage_prices WHERE upstream_version=$version ORDER BY model", ("$version", version));
        using var reader = command.ExecuteReader(); var prices = new List<UsagePrice>();
        while (reader.Read()) prices.Add(JsonSerializer.Deserialize<UsagePrice>(reader.GetString(0))!);
        return prices;
    }
    public void Save(long version, string model, string currency, decimal? input, decimal? output, decimal? read, decimal? write)
    {
        model = model.Trim(); currency = currency.Trim().ToUpperInvariant();
        if (model.Length is < 1 or > 160 || model.Any(c => !(char.IsAsciiLetterOrDigit(c) || "-_.:/@".Contains(c)))
            || !ValidCurrency(currency) || new[] { input, output, read, write }.Any(value => value is < 0 or > 1000000)
            || new[] { input, output, read, write }.All(value => value is null))
            throw new InvalidOperationException("请填写有效模型、三位币种代码和至少一项非负单价；未知单价留空。");
        var price = new UsagePrice(Guid.NewGuid().ToString("N"), version, model, currency, input, output, read, write);
        database.Write((connection, transaction) =>
        {
            if (Convert.ToInt64(Scalar(connection, transaction, "SELECT version FROM upstream WHERE id=1")) != version)
                throw new InvalidOperationException("活动上游已改变，请刷新后重新确认单价。");
            return Execute(connection, transaction, """
                INSERT INTO usage_prices(id,upstream_version,model,json) VALUES($id,$version,$model,$json)
                ON CONFLICT(upstream_version,model) DO UPDATE SET id=excluded.id,json=excluded.json
                """, ("$id", price.Id), ("$version", version), ("$model", model), ("$json", JsonSerializer.Serialize(price)));
        });
    }
    internal static bool ValidCurrency(string? value) => value is { Length: 3 } && value.All(c => c is >= 'A' and <= 'Z');
    internal static string? DecimalText(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);

    internal static UsageMoney Calculate(string kind, UsageEnd end, string? priceJson)
    {
        if (!end.Dispatched || kind != "inference") return new(Source: "not_applicable", Completeness: "not_applicable");
        var complete = end.Outcome == "succeeded" && end.Completeness == "complete";
        if (end.ReportedMoney is { Amount: not null } reported)
            return reported with { Completeness = end.Outcome == "succeeded" && reported.Completeness == "complete" ? "complete" : "partial" };
        if (priceJson is null) return new();
        var price = JsonSerializer.Deserialize<UsagePrice>(priceJson)!;
        decimal? amount = null; var missing = false;
        foreach (var (tokens, rate) in new[] { (end.Tokens.Input, price.InputPerMillion), (end.Tokens.Output, price.OutputPerMillion),
            (end.Tokens.CacheRead, price.CacheReadPerMillion), (end.Tokens.CacheWrite, price.CacheWritePerMillion) })
        {
            if (tokens == 0) { amount ??= 0; continue; }
            if (rate == 0) { if (tokens.HasValue) amount ??= 0; continue; }
            if (tokens is null || rate is null) { missing = true; continue; }
            amount = (amount ?? 0) + tokens.Value * rate.Value / 1000000m;
        }
        return amount is null ? new() : new(decimal.Round(amount.Value, 9, MidpointRounding.AwayFromZero), price.Currency, "estimated", complete && !missing ? "complete" : "partial");
    }

    internal static UsageMoney? ReadReported(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object) return null;
        // Anthropic Messages 没有标准金额字段；这些是明确带币种的可选兼容扩展，不推测无单位 cost 的币种。
        if (usage.TryGetProperty("cost_usd", out var usd) && Amount(usd) is { } dollars) return new(dollars, "USD", "reported");
        if (!usage.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object
            || !cost.TryGetProperty("amount", out var value) || Amount(value) is not { } amount
            || !cost.TryGetProperty("currency", out var code) || code.ValueKind != JsonValueKind.String) return null;
        var currency = code.GetString()?.ToUpperInvariant();
        return ValidCurrency(currency) ? new(amount, currency, "reported") : null;
    }
    private static decimal? Amount(JsonElement value)
    {
        decimal amount;
        var valid = value.ValueKind == JsonValueKind.Number ? value.TryGetDecimal(out amount)
            : decimal.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : null,
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount);
        return valid && amount is >= 0 and <= 1000000000 ? decimal.Round(amount, 9, MidpointRounding.AwayFromZero) : null;
    }
}
