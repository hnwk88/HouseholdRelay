using System.Text.Json;
using Application.Interfaces.Usage;
using Core.Usage;
using Infrastructure.Persistence;
using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Repositories.Usage;

/// <summary>usage_prices 表的读写；单价按已保存上游主键与模型唯一，主键由库生成，保存时在同一事务里核对那一家仍存在。JSON 只装费率，归属看 candidate_id 列。</summary>
internal sealed class UsagePriceRepository(RelayDatabase database) : IUsagePriceRepository
{
    /// <summary>列出某个已保存上游下的单价。</summary>
    public IReadOnlyList<UsagePrice> List(long candidateId)
    {
        using var connection = database.Open();
        using var command = Command(connection, null, "SELECT json FROM usage_prices WHERE candidate_id=$candidate ORDER BY model", ("$candidate", candidateId));
        using var reader = command.ExecuteReader(); var prices = new List<UsagePrice>();
        while (reader.Read()) prices.Add(JsonSerializer.Deserialize<UsagePrice>(reader.GetString(0))!);
        return prices;
    }

    /// <summary>那一家仍存在时写入或覆盖单价；覆盖时主键不变，只换费率。</summary>
    public void Save(long candidateId, UsagePrice price) => database.Write((connection, transaction) =>
    {
        if (Scalar(connection, transaction, "SELECT 1 FROM upstream_candidates WHERE id=$candidate AND deleted_at IS NULL", ("$candidate", candidateId)) is null)
            throw new InvalidOperationException("这个上游已不存在，请刷新后重新选择上游再填写单价。");
        return Execute(connection, transaction, """
            INSERT INTO usage_prices(candidate_id,model,json) VALUES($candidate,$model,$json)
            ON CONFLICT(candidate_id,model) DO UPDATE SET json=excluded.json
            """, ("$candidate", candidateId), ("$model", price.Model), ("$json", JsonSerializer.Serialize(price)));
    });
}
