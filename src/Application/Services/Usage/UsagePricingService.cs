using Application.DependencyInjection;
using Application.Interfaces.Usage;
using Core.Upstream;
using Core.Usage;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Services.Usage;

/// <summary>按已保存上游（主键）和模型保存费率；发送时复制快照，之后改价不改变历史金额。</summary>
public sealed class UsagePricingService(IServiceScopeFactory scopes) : IUsagePricingService
{
    /// <summary>列出指定已保存上游下的全部模型单价，按模型名排序。</summary>
    public async Task<IReadOnlyList<UsagePrice>> ListAsync(long upstreamCandidateId)
    {
        await using var scope = scopes.CreateAsyncScope();
        var rows = await scope.Repository<UsagePriceRecord>().FindAsync(item => item.UpstreamCandidateId == upstreamCandidateId);
        return rows.OrderBy(item => item.Model, StringComparer.Ordinal).Select(item => item.Price()).ToArray();
    }

    /// <summary>
    /// 保存某个已保存上游某模型的每百万计数单价：模型名去空白后须合乎 <see cref="ModelName"/>；币种转大写后须为三位字母；
    /// 各项单价须在 0–1000000 之间且至少填一项，未知单价留空（null）。同一上游同一模型再存一次就覆盖费率（主键不变）。
    /// 那一家已不在（停用或不存在）时拒绝；核对与写入在同一个事务里。不合法时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public async Task SaveAsync(long upstreamCandidateId, string model, string currency, decimal? input, decimal? output, decimal? read, decimal? write)
    {
        model = model.Trim(); currency = currency.Trim().ToUpperInvariant();
        if (!ModelName.IsValid(model)
            || !UsageMoneyRules.ValidCurrency(currency) || new[] { input, output, read, write }.Any(value => value is < 0 or > 1000000)
            || new[] { input, output, read, write }.All(value => value is null))
            throw new InvalidOperationException("请填写有效模型、三位币种代码和至少一项非负单价；未知单价留空。");
        var price = new UsagePrice(model, currency, input, output, read, write);
        await using var scope = scopes.CreateAsyncScope();
        var prices = scope.Repository<UsagePriceRecord>();
        await prices.ExecuteInTransactionAsync(async () =>
        {
            if (!await scope.Repository<UpstreamCandidate>().AnyAsync(item => item.Id == upstreamCandidateId))
                throw new InvalidOperationException("这个上游已不存在，请刷新后重新选择上游再填写单价。");
            var existing = await prices.FirstOrDefaultAsync(item => item.UpstreamCandidateId == upstreamCandidateId && item.Model == price.Model);
            if (existing is null)
            {
                existing = new UsagePriceRecord { UpstreamCandidateId = upstreamCandidateId, Model = price.Model };
                await prices.AddAsync(existing);
            }
            existing.Replace(price);
            return await prices.SaveChangesAsync();
        });
    }
}
