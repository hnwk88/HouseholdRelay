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

    /// <summary>错误类型的中文名：上游常见的 error.type 与本中转自己的分类；不认识的上游类型原样返回。</summary>
    public static string ErrorType(string value) => value switch
    {
        "overloaded_error" => "上游过载", "api_error" => "上游内部错误", "rate_limit_error" => "上游限流", "authentication_error" => "上游认证失败",
        "permission_error" => "上游拒绝访问", "not_found_error" => "上游找不到", "invalid_request_error" => "请求无效", "request_too_large" => "请求过大",
        "billing_error" => "上游计费问题", "timeout_error" => "上游超时",
        "unreadable" => "错误正文无法解析", "unrecognized" => "错误格式不认识", "connection_failed" => "连不上上游", "timeout" => "等待上游超时",
        "interrupted" => "回传中断", "protocol" => "上游协议异常", "cancelled" => "客户端已取消", "relay_error" => "中转内部错误", _ => value
    };

    /// <summary>连接失败分类的中文名；未知值原样返回。</summary>
    public static string ConnectFailure(string value) => value switch
    { "dns" => "域名解析失败", "refused" => "拒绝连接", "tls" => "TLS 握手失败", "connect_timeout" => "建连超时", "proxy" => "代理不可用", "other" => "其他连接错误", _ => value };

    /// <summary>
    /// 把一次请求的上游尝试读成一句话，按读屏顺序写「哪家，状态码，错误类型，说明，耗时」，例如「顺丰中转，502，上游过载，Overloaded，15.2 秒」；
    /// 多次尝试按次序以「第 N 次：」开头、以分号隔开；没有尝试时读「未发往上游」。上游已删除或不是从已保存上游切换来的，分别读「已删除的上游」「未命名上游」。
    /// </summary>
    public static string Attempts(IReadOnlyList<UpstreamAttemptRow> attempts)
    {
        if (attempts.Count == 0) return "未发往上游";
        return string.Join("；", attempts.Select(attempt =>
        {
            var parts = new List<string> { attempt.Name ?? (attempt.CandidateId is null ? "未命名上游" : "已删除的上游"), attempt.UpstreamStatus?.ToString() ?? "无响应" };
            if (attempt.ErrorType is { } type) parts.Add(attempt.ConnectFailure is { } failure ? ErrorType(type) + "：" + ConnectFailure(failure) : ErrorType(type));
            if (attempt.ErrorMessage is { } message) parts.Add(message);
            parts.Add((attempt.DurationMs / 1000d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " 秒");
            if (attempt.Disposition == "next") parts.Add("换下一家");
            return (attempts.Count > 1 ? "第 " + attempt.Number + " 次：" : "") + string.Join("，", parts);
        }));
    }

    /// <summary>把一笔金额格式化为「数值 币种 · 来源」，最多 9 位小数，部分完整时追加「（部分）」；不适用和金额未知分别显示对应文字。</summary>
    public static string Money(UsageMoney value) => value.Source == "not_applicable" ? "不适用" : value.Amount is null ? "未知"
        : value.Amount.Value.ToString("0.#########") + " " + value.Currency + " · " + MoneySource(value.Source) + (value.Completeness == "partial" ? "（部分）" : "");
}
