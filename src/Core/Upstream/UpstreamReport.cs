using System.Text.Json.Serialization;

namespace Core.Upstream;

/// <summary>仅保存安全的检测状态；不保存上游原始响应或凭据。</summary>
public sealed record UpstreamReport(DateTimeOffset CheckedAt, string Connection, string Authentication, string ModelDiscovery,
    string Call, string[] Models, string? VerifiedModel, string Message)
{
    /// <summary>只有最小模型调用检测通过时才允许把该候选激活为活动版本；不参与序列化。</summary>
    [JsonIgnore] public bool CanActivate => Call == "passed";

    /// <summary>把检测状态码转换为界面显示的中文说明；未知状态一律显示「未检测」。</summary>
    public static string Label(string status) => status switch
    {
        "passed" => "通过", "failed" => "失败", "unsupported" => "未提供模型目录", "unverified" => "未检测",
        "reconnect" => "凭据失效，需重新接入", "cooldown" => "请求过多，稍后重试", "cancelled" => "检测已取消", _ => "未检测"
    };
}
