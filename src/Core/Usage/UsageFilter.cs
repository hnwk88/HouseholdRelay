namespace Core.Usage;

/// <summary>两种入口共用筛选合同；个人身份由服务器覆盖，不能由筛选参数提供。</summary>
public sealed record UsageFilter(string? UserId = null, DateTimeOffset? From = null, DateTimeOffset? Until = null,
    string? Kind = null, string? Model = null, string? Outcome = null, int Page = 1, string Granularity = "day", string TimeZone = "UTC")
{
    /// <summary>明细分页的固定每页行数，不允许由查询参数调整。</summary>
    public const int PageSize = 25;

    /// <summary>
    /// 校验筛选条件：页码 1–1000000、起止时间不倒置、模型名不超过 160 字符、用户编号不超过 64 字符、粒度只能为 day 或 hour，
    /// 接口类别与结果只能取已知值（空表示不筛选），时区必须是本机可识别的时区标识。不合法时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public void Validate()
    {
        if (Page is < 1 or > 1000000 || From > Until || Model?.Length > 160 || UserId?.Length > 64
            || Granularity is not ("day" or "hour") || TimeZone.Length is < 1 or > 128
            || Kind is not (null or "" or "inference" or "estimate" or "models")
            || Outcome is not (null or "" or "started" or "succeeded" or "failed" or "cancelled" or "interrupted"))
            throw new InvalidOperationException("查询条件无效，请检查时间、接口、结果和页码。");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZone); }
        // 只把「时区不存在或数据损坏」转成统一的输入错误提示，其他异常（如权限或系统故障）不吞掉，继续向上抛出。
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new InvalidOperationException("查询时区无效。"); }
    }
}
