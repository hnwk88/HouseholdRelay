using System.Text.Json;
using Repositories;

namespace Core.Usage;

/// <summary>某一家已保存上游某个模型的单价（表 usage_prices，同一家同一模型唯一）。JSON 只装费率（<see cref="UsagePrice"/>），归属看 <see cref="UpstreamCandidateId"/>。</summary>
public sealed class UsagePriceRecord : BaseEntity
{
    /// <summary>单价属于的已保存上游主键。</summary>
    public long UpstreamCandidateId { get; set; }

    /// <summary>模型名。</summary>
    public string Model { get; set; } = "";

    /// <summary>费率 JSON；发送时整份复制进用量记录作快照。</summary>
    public string Json { get; set; } = "";

    /// <summary>读出费率。</summary>
    public UsagePrice Price() => JsonSerializer.Deserialize<UsagePrice>(Json)!;

    /// <summary>换成新费率；主键与归属不变。</summary>
    /// <param name="price">新费率。</param>
    public void Replace(UsagePrice price) => Json = JsonSerializer.Serialize(price);
}
