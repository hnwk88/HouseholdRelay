using Core.Values;

namespace Core.Usage;

/// <summary>
/// 用量取值的显示名表，随用量报告一起交给页面脚本：键是库里、JSON 里的线上名，值取自 <see cref="UsageLabels"/>。
/// 个人用量页的脚本只查这张表，不再自己抄一份中文名。
/// </summary>
public sealed class UsageLabelTable
{
    /// <summary>唯一的一份；内容只由取值表和标签决定，不随请求变化。</summary>
    public static UsageLabelTable Instance { get; } = new();

    /// <summary>只经 <see cref="Instance"/> 取。</summary>
    private UsageLabelTable() { }

    /// <summary>接口类别的显示名。</summary>
    public IReadOnlyDictionary<string, string> Kinds { get; } = Table<UsageKind>(UsageLabels.Kind);

    /// <summary>请求结果的显示名。</summary>
    public IReadOnlyDictionary<string, string> Outcomes { get; } = Table<UsageOutcome>(UsageLabels.Outcome);

    /// <summary>计数完整性的显示名。</summary>
    public IReadOnlyDictionary<string, string> Completeness { get; } = Table<UsageCompleteness>(UsageLabels.Completeness);

    /// <summary>金额来源的显示名。</summary>
    public IReadOnlyDictionary<string, string> MoneySources { get; } = Table<MoneySource>(UsageLabels.MoneySource);

    /// <summary>币种代码的中文名（与 <see cref="UsageLabels.Currency"/> 同一份）；脚本查不到的代码原样读出。</summary>
    public IReadOnlyDictionary<string, string> Currencies { get; } = UsageLabels.CurrencyNames;

    /// <summary>按声明顺序把每个取值的线上名对到它的显示名。</summary>
    private static IReadOnlyDictionary<string, string> Table<TEnum>(Func<TEnum, string> label) where TEnum : struct, Enum =>
        Wire<TEnum>.Values.ToDictionary(Wire<TEnum>.Name, label, StringComparer.Ordinal);
}
