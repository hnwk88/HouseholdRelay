namespace Application.Interfaces.ClaudeCode;

/// <summary>把站点权威身份准备到工程私有的 Claude 配置；普通请求只校验，不在转发中改写文件。</summary>
public interface IClaudeConfigurationPreparation
{
    /// <summary>持久化的站点设备编号；读取不会生成或更换编号。</summary>
    string DeviceId { get; }

    /// <summary>受控准备一个账户的私有配置；仅新建账户允许创建缺失的配置文件。</summary>
    void PrepareAccount(string accountId, bool allowCreate = false);

    /// <summary>
    /// 在读取可能续期的令牌之前核对账户配置；缺失、损坏、被占用或身份不一致时拒绝。
    /// 同一代身份下核对过的账户不再打开文件（准备、维护、登录、导入后失效）；<paramref name="reread"/> 为真时重读，供启动续期程序之前用。
    /// </summary>
    void ValidateAccount(string accountId, bool reread = false);

    /// <summary>为本次隔离回环诊断准备设备编号；目录必须属于工程命名的临时诊断目录。</summary>
    void PrepareTransient(string configDirectory);

    /// <summary>受控启动或维护时对齐全部既有私有账户；不读取或更改凭据文件。</summary>
    void PrepareExistingAccounts(CancellationToken cancellation = default);
}
