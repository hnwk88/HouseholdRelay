using System.Globalization;
using System.Text.Json;

namespace Core.Usage;

/// <summary>金额的纯计算规则：按发送时的单价快照估算、读取上游报告金额、校验币种和十进制文本格式。</summary>
public static class UsageMoneyRules
{
    /// <summary>判断是否为三位大写英文字母的币种代码（如 USD）；只校验形式，不核对是否为真实存在的 ISO 币种。</summary>
    public static bool ValidCurrency(string? value) => value is { Length: 3 } && value.All(c => c is >= 'A' and <= 'Z');

    /// <summary>把金额转成与区域无关的十进制文本，便于持久化；null 保持为 null。</summary>
    public static string? DecimalText(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 计算一次请求的金额：未发往上游或非推理接口为不适用；有上游报告金额时优先使用并按请求结果判定完整性；
    /// 否则用发送时的单价快照 JSON（每百万计数单价）按四类计数估算，保留 9 位小数。没有单价快照或所有需要计费的项都缺数据时返回未知金额；
    /// 部分计数或单价缺失时只累加已知部分并标记为部分完整。
    /// </summary>
    public static UsageMoney Calculate(string kind, UsageEnd end, string? priceJson)
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

    /// <summary>从 usage 对象读取上游明确报告的金额：优先 <c>cost_usd</c>（按美元），其次带合法币种的 <c>cost.amount</c> + <c>cost.currency</c>；都没有或不合法时返回 null。</summary>
    public static UsageMoney? ReadReported(JsonElement usage)
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
    /// <summary>把数字或数字字符串解析为金额，只接受 0 到 10 亿之间的值并四舍五入到 9 位小数；无法解析、为负或超出范围时返回 null。</summary>
    private static decimal? Amount(JsonElement value)
    {
        decimal amount;
        var valid = value.ValueKind == JsonValueKind.Number ? value.TryGetDecimal(out amount)
            : decimal.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : null,
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount);
        return valid && amount is >= 0 and <= 1000000000 ? decimal.Round(amount, 9, MidpointRounding.AwayFromZero) : null;
    }
}
