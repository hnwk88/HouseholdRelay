using System.Text.Json;
using Application.Interfaces.Usage;
using Core.Usage;
using Infrastructure.Persistence;
using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Repositories.Usage;

/// <summary>usage_prices 表的读写；保存时在同一事务里核对活动上游版本。</summary>
internal sealed class UsagePriceRepository(RelayDatabase database) : IUsagePriceRepository
{
    /// <summary>列出某个上游版本下的单价。</summary>
    public IReadOnlyList<UsagePrice> List(long version)
    {
        using var connection = database.Open();
        using var command = Command(connection, null, "SELECT json FROM usage_prices WHERE upstream_version=$version ORDER BY model", ("$version", version));
        using var reader = command.ExecuteReader(); var prices = new List<UsagePrice>();
        while (reader.Read()) prices.Add(JsonSerializer.Deserialize<UsagePrice>(reader.GetString(0))!);
        return prices;
    }

    /// <summary>活动版本匹配时写入或覆盖单价。</summary>
    public void Save(UsagePrice price) => database.Write((connection, transaction) =>
    {
        if (Convert.ToInt64(Scalar(connection, transaction, "SELECT version FROM upstream WHERE id=1")) != price.UpstreamVersion)
            throw new InvalidOperationException("活动上游已改变，请刷新后重新确认单价。");
        return Execute(connection, transaction, """
            INSERT INTO usage_prices(id,upstream_version,model,json) VALUES($id,$version,$model,$json)
            ON CONFLICT(upstream_version,model) DO UPDATE SET id=excluded.id,json=excluded.json
            """, ("$id", price.Id), ("$version", price.UpstreamVersion), ("$model", price.Model), ("$json", JsonSerializer.Serialize(price)));
    });
}
