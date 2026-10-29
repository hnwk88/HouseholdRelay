namespace Core.Usage;

/// <summary>用量页面使用的中文标签：接口类别、结果、完整性、计数和金额来源。</summary>
public static class UsageLabels
{
    /// <summary>接口类别的中文名（推理、估算、模型查询）；未知值原样返回。</summary>
    public static string Kind(string value) => value switch { "inference" => "推理", "estimate" => "估算", "models" => "模型查询", _ => value };

    /// <summary>请求结果的中文名；started 表示仍在处理或结束记录尚未写入。未知值原样返回。</summary>
    public static string Outcome(string value) => value switch
    { "started" => "处理中 / 待写入", "succeeded" => "成功", "failed" => "失败", "cancelled" => "已取消", "interrupted" => "中断", _ => value };

    /// <summary>用量完整性的中文名；未知值原样返回。</summary>
    public static string Completeness(string value) => value switch
    { "complete" => "完整", "partial" => "部分 / 不完整", "not_provided" => "上游未提供", "not_applicable" => "不适用", _ => value };

    /// <summary>按当前区域格式显示带千分位的计数；null 显示为「未知」而不是 0。</summary>
    public static string Count(long? value) => value?.ToString("N0") ?? "未知";

    /// <summary>金额来源的中文名；不认识的来源一律显示「未知」。</summary>
    public static string MoneySource(string value) => value switch { "reported" => "上游报告", "estimated" => "估算", "not_applicable" => "不适用", _ => "未知" };

    /// <summary>把一笔金额格式化为「数值 币种 · 来源」，最多 9 位小数，部分完整时追加「（部分）」；不适用和金额未知分别显示对应文字。</summary>
    public static string Money(UsageMoney value) => value.Source == "not_applicable" ? "不适用" : value.Amount is null ? "未知"
        : value.Amount.Value.ToString("0.#########") + " " + value.Currency + " · " + MoneySource(value.Source) + (value.Completeness == "partial" ? "（部分）" : "");
}
