namespace Application.Interfaces.Relay;

/// <summary>管理员临时启用的限时限量脱敏取证，不读取或复制完整正文。</summary>
public interface IProtocolDiagnostics
{
    /// <summary>当前采集状态和剩余预算的读屏说明。</summary>
    string Status { get; }
    /// <summary>启动至多十分钟和一百条样本的采集，重启后默认关闭。</summary>
    void Start();
    /// <summary>立即停止采集。</summary>
    void Stop();
    /// <summary>仅接收已分类值和设备号用于哈希；不接受正文、账户、令牌、目录或通用请求头。</summary>
    void Record(string correlation, string stage, string route, long generation, string deviceId,
        string? version = null, string? entrypoint = null, int? status = null, string? error = null);
}
