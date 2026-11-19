namespace Core.Configuration;

/// <summary>一次数据库升级里某一版迁移另存进归档表的一类数据：哪一版、原来在哪张表、为什么没留在原处、几条。</summary>
/// <param name="SchemaVersion">把它另存的那一版迁移（升到的版本号）。</param>
/// <param name="Table">原来所在的表名。</param>
/// <param name="Reason">另存原因代码，见 <see cref="Sentence"/> 的读法。</param>
/// <param name="Count">条数。</param>
public sealed record ArchivedRows(int SchemaVersion, string Table, string Reason, long Count)
{
    /// <summary>读屏顺序的一句话，例如「版本 9：指向已删除上游的单价 3 条，整行另存，不再参与计价」。不认识的原因代码原样读出。</summary>
    public string Sentence() => $"版本 {SchemaVersion}：" + Reason switch
    {
        "orphan-price" => $"指向已删除上游的单价 {Count} 条，整行另存，不再参与计价",
        "orphan-reference" => $"{Table switch { "usage_requests" => "用量记录", "usage_attempts" => "上游尝试", _ => "活动上游" }}里指向已删除上游的引用 {Count} 条，原主键另存、引用清空，记录本身保留",
        "price-not-migrated" => $"旧活动版本的单价 {Count} 条，改按上游计价后查不到，整行另存",
        "dropped-proxy" => $"没被选作全局代理的旧代理地址 {Count} 个，原值另存",
        "ambiguous-active-source" => $"活动上游有多家已保存上游对得上，没有自动认定，候选主键另存 {Count} 条，请在上游页切换一次确认",
        _ => $"{Table} 表 {Reason} {Count} 条"
    };
}
