namespace Core.Usage;

/// <summary>用量页面使用的中文标签：接口类别、结果、完整性、计数和金额来源。封闭取值的映射写全每个取值，不兜底；新增取值时编译器会报。</summary>
public static class UsageLabels
{
    /// <summary>接口类别的中文名（推理、估算、模型查询）。</summary>
    public static string Kind(UsageKind value) => value switch { UsageKind.Inference => "推理", UsageKind.Estimate => "估算", UsageKind.Models => "模型查询" };

    /// <summary>请求结果的中文名；started 表示仍在处理或结束记录尚未写入。</summary>
    public static string Outcome(UsageOutcome value) => value switch
    {
        UsageOutcome.Started => "处理中 / 待写入", UsageOutcome.Succeeded => "成功", UsageOutcome.Failed => "失败",
        UsageOutcome.Cancelled => "已取消", UsageOutcome.Interrupted => "中断"
    };

    /// <summary>用量完整性的中文名。</summary>
    public static string Completeness(UsageCompleteness value) => value switch
    {
        UsageCompleteness.Complete => "完整", UsageCompleteness.Partial => "部分 / 不完整",
        UsageCompleteness.NotProvided => "上游未提供", UsageCompleteness.NotApplicable => "不适用"
    };

    /// <summary>按当前区域格式显示带千分位的计数，不带单位（单位写在表头或读句里）；null 显示为「未知」而不是 0。</summary>
    public static string Count(long? value) => value?.ToString("N0") ?? "未知";

    /// <summary>token 计数的读法：「12,345 个 token」；null 读「未知」，不读成 0 个。</summary>
    public static string Tokens(long? value) => value is null ? "未知" : Count(value) + " 个 token";

    /// <summary>请求次数的读法：「30 次」。</summary>
    public static string Times(long value) => Count(value) + " 次";

    /// <summary>
    /// 币种代码到中文名的唯一一份映射，C# 页面经 <see cref="Currency"/> 读，页面脚本经 <see cref="UsageLabelTable.Currencies"/> 读。
    /// 只收常见的几种；认不出的代码原样读出，不猜。
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> CurrencyNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["USD"] = "美元", ["CNY"] = "人民币", ["EUR"] = "欧元", ["GBP"] = "英镑", ["JPY"] = "日元", ["HKD"] = "港元",
    };

    /// <summary>币种的中文名，例如 USD 读「美元」；认不出的代码原样读出。</summary>
    public static string Currency(string code) => CurrencyNames.GetValueOrDefault(code, code);

    /// <summary>金额数加中文币种名：「0.53 美元」，最多 9 位小数。</summary>
    public static string Amount(decimal amount, string currency) => amount.ToString("0.#########") + " " + Currency(currency);

    /// <summary>金额来源的中文名；来源未知（没有报告也没有单价）读「未知」。</summary>
    public static string MoneySource(MoneySource value) => value switch
    {
        Usage.MoneySource.Reported => "上游报告", Usage.MoneySource.Estimated => "估算",
        Usage.MoneySource.NotApplicable => "不适用", Usage.MoneySource.Unknown => "未知"
    };

    /// <summary>上游常见的 error.type 与本中转自己的分类各自的中文名。</summary>
    private static readonly Dictionary<string, string> _errorTypes = new(StringComparer.Ordinal)
    {
        ["overloaded_error"] = "上游过载", ["api_error"] = "上游内部错误", ["rate_limit_error"] = "上游限流", ["authentication_error"] = "上游认证失败",
        ["permission_error"] = "上游拒绝访问", ["not_found_error"] = "上游找不到", ["invalid_request_error"] = "请求无效", ["request_too_large"] = "请求过大",
        ["billing_error"] = "上游计费问题", ["timeout_error"] = "上游超时",
        ["unreadable"] = "错误正文无法解析", ["unrecognized"] = "错误格式不认识", ["connection_failed"] = "连不上上游", ["timeout"] = "等待上游超时",
        ["interrupted"] = "回传中断", ["protocol"] = "上游协议异常", ["cancelled"] = "客户端已取消", ["identity_unavailable"] = "本机 Claude Code 身份不可用", ["relay_error"] = "中转内部错误",
    };

    /// <summary>
    /// 错误类型的中文名。这里收的是上游自报的 error.type 原值，取值集由上游决定、不是封闭取值，所以查表而不是写成封闭 switch：
    /// 认识的读中文名，不认识的上游类型原样读出，让管理员听到上游的原话。
    /// </summary>
    public static string ErrorType(string value) => _errorTypes.GetValueOrDefault(value, value);

    /// <summary>连接失败分类的中文名。</summary>
    public static string ConnectFailure(ConnectFailure value) => value switch
    {
        Usage.ConnectFailure.Dns => "域名解析失败", Usage.ConnectFailure.Refused => "拒绝连接", Usage.ConnectFailure.Tls => "TLS 握手失败",
        Usage.ConnectFailure.ConnectTimeout => "建连超时", Usage.ConnectFailure.Proxy => "代理不可用", Usage.ConnectFailure.Other => "其他连接错误"
    };

    /// <summary>
    /// 把一次请求的上游尝试读成一句话，按读屏顺序写「哪家，状态码，错误类型，说明，耗时」，例如「顺丰中转，502，上游过载，Overloaded，15.2 秒」；
    /// 多次尝试按次序以「第 N 次：」开头、以分号隔开；没有尝试时读「未发往上游」。上游后来被删除（停用）的读「名字（已删除）」；
    /// 不是从已保存上游切换来的或那一家没有名字的读「未命名上游」；查不到名字的旧记录读「已删除的上游」。
    /// </summary>
    public static string Attempts(IReadOnlyList<UpstreamAttemptRow> attempts)
    {
        if (attempts.Count == 0) return "未发往上游";
        return string.Join("；", attempts.Select(attempt =>
        {
            var name = attempt.Name is { Length: > 0 } named ? attempt.Deleted ? named + "（已删除）" : named
                : attempt.Name is not null || attempt.UpstreamCandidateId is null ? "未命名上游" : "已删除的上游";
            var parts = new List<string> { name, attempt.UpstreamStatus?.ToString() ?? "无响应" };
            if (attempt.ErrorType is { } type) parts.Add(attempt.ConnectFailure is { } failure ? ErrorType(type) + "：" + ConnectFailure(failure) : ErrorType(type));
            if (attempt.ErrorMessage is { } message) parts.Add(message);
            parts.Add((attempt.DurationMs / 1000d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " 秒");
            if (attempt.Disposition == AttemptDisposition.Next) parts.Add("换下一家");
            return (attempts.Count > 1 ? "第 " + attempt.Number + " 次：" : "") + string.Join("，", parts);
        }));
    }

    /// <summary>把一笔金额格式化为「数值 中文币种名 · 来源」，最多 9 位小数，部分完整时追加「（部分）」；不适用和金额未知分别显示对应文字。</summary>
    public static string Money(UsageMoney value) => value.Source == Usage.MoneySource.NotApplicable ? "不适用" : value.Amount is null ? "未知"
        : Amount(value.Amount.Value, value.Currency ?? "") + " · " + MoneySource(value.Source) + (value.Completeness == MoneyCompleteness.Partial ? "（部分）" : "");
}
