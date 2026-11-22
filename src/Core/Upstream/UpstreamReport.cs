using System.Text.Json.Serialization;

namespace Core.Upstream;

/// <summary>仅保存安全的检测状态；不保存上游原始响应或凭据。</summary>
public sealed record UpstreamReport(DateTimeOffset CheckedAt, CheckStatus Connection, CheckStatus Authentication, CheckStatus ModelDiscovery,
    CheckStatus Call, string[] Models, string? VerifiedModel, string Message)
{
    /// <summary>只有最小模型调用检测通过时才允许把该候选激活为活动版本；不参与序列化。</summary>
    [JsonIgnore] public bool CanActivate => Call == CheckStatus.Passed;

    /// <summary>「检测未完成」读句的开头；检测没有走到结论（出错、被取消、服务重启、超过检测窗口）时，读句都以它开头。</summary>
    public const string UnfinishedLead = "检测未完成：";

    /// <summary>检测没有走到结论时的下一步，接在原因后面。</summary>
    public const string UnfinishedNext = "可以重新获取模型或测试，也可以删除它。";

    /// <summary>管理员按了取消。</summary>
    public const string CancelledReason = "管理员取消了这次检测。";

    /// <summary>检测途中出了没有预料到的错误；错误类型记进日志，读句不带细节。</summary>
    public const string FaultReason = "检测途中出现意外错误，错误类型已记进数据目录 logs 文件夹里的错误日志。";

    /// <summary>服务重启时检测还没结束。</summary>
    public const string RestartReason = "服务重启时这次检测还没有结束。";

    /// <summary>停在检测中超过检测窗口。</summary>
    public const string WindowReason = "上次检测超过两分钟没有结束。";

    /// <summary>检测没有走到结论时存下的报告：各项都未验证、没有模型目录，读句是「检测未完成：原因。下一步」。</summary>
    /// <param name="at">写下的时刻。</param>
    /// <param name="reason">原因（一句话，以句号结尾）。</param>
    public static UpstreamReport Unfinished(DateTimeOffset at, string reason) =>
        new(at, CheckStatus.Unverified, CheckStatus.Unverified, CheckStatus.Unverified, CheckStatus.Unverified, [], null, UnfinishedLead + reason + UnfinishedNext);
}
