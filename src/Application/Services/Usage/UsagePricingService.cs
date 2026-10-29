using Application.Interfaces.Usage;
using Core.Usage;

namespace Application.Services.Usage;

/// <summary>按活动上游版本和模型保存费率；发送时复制快照，之后改价不改变历史金额。</summary>
public sealed class UsagePricingService(IUsagePriceRepository repository) : IUsagePricingService
{
    /// <summary>列出指定上游版本下的全部模型单价。</summary>
    public IReadOnlyList<UsagePrice> List(long version) => repository.List(version);

    /// <summary>
    /// 保存某版本某模型的每百万计数单价：模型名去空白后须为 1–160 个字符，只含字母数字和 <c>- _ . : / @</c>；币种转大写后须为三位字母；
    /// 各项单价须在 0–1000000 之间且至少填一项，未知单价留空（null）。每次保存都生成新的单价记录。不合法时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public void Save(long version, string model, string currency, decimal? input, decimal? output, decimal? read, decimal? write)
    {
        model = model.Trim(); currency = currency.Trim().ToUpperInvariant();
        if (model.Length is < 1 or > 160 || model.Any(c => !(char.IsAsciiLetterOrDigit(c) || "-_.:/@".Contains(c)))
            || !UsageMoneyRules.ValidCurrency(currency) || new[] { input, output, read, write }.Any(value => value is < 0 or > 1000000)
            || new[] { input, output, read, write }.All(value => value is null))
            throw new InvalidOperationException("请填写有效模型、三位币种代码和至少一项非负单价；未知单价留空。");
        repository.Save(new UsagePrice(Guid.NewGuid().ToString("N"), version, model, currency, input, output, read, write));
    }
}
