using Core.Upstream;
using Core.Values;

namespace Core.Usage;

/// <summary>两种入口共用筛选合同；个人身份由服务器覆盖，不能由筛选参数提供。</summary>
public sealed record UsageFilter(long? UserId = null, DateTimeOffset? From = null, DateTimeOffset? Until = null,
    UsageKind? Kind = null, string? Model = null, UsageOutcome? Outcome = null, int Page = 1, string Granularity = "day", string TimeZone = "UTC")
{
    /// <summary>明细分页的固定每页行数，不允许由查询参数调整。</summary>
    public const int PageSize = 25;

    /// <summary>
    /// 校验筛选条件：页码 1–1000000、起止时间不倒置、模型名不超过 <see cref="ModelName.MaxLength"/> 字符、使用者主键为正数、粒度只能为 day 或 hour，
    /// 接口类别与结果只能取已知值（空表示不筛选），时区必须是本机可识别的时区标识。不合法时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public void Validate()
    {
        if (Page is < 1 or > 1000000 || From > Until || Model?.Length > ModelName.MaxLength || UserId is < 1
            || Granularity is not ("day" or "hour") || TimeZone.Length is < 1 or > 128)
            throw Invalid();
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZone); }
        // 只把「时区不存在或数据损坏」转成统一的输入错误提示，其他异常（如权限或系统故障）不吞掉，继续向上抛出。
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new InvalidOperationException("查询时区无效。"); }
    }

    /// <summary>
    /// 把查询参数里的接口类别或结果（线上名）读成封闭取值：空串或没给表示不筛选；不认识时抛出与 <see cref="Validate"/> 同一句的 <see cref="InvalidOperationException"/>。
    /// 管理页与个人用量入口共用这一处解析。
    /// </summary>
    public static TValue? Closed<TValue>(string? text) where TValue : struct, Enum =>
        string.IsNullOrEmpty(text) ? null : Wire<TValue>.TryParse(text, out var value) ? value : throw Invalid();

    /// <summary>筛选条件不合法时的读句。</summary>
    private static InvalidOperationException Invalid() => new("查询条件无效，请检查时间、接口、结果和页码。");
}
