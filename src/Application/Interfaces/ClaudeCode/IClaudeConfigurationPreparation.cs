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

    /// <summary>读出官方账户当前的 oauthAccount.accountUuid，供对齐发往官方的账户段；本代身份下命中缓存不再读文件，缺失或格式不对时拒绝。</summary>
    string AccountUuid(string accountId);

    /// <summary>
    /// 把官方返回的账户资料照原生登录写进账户私有配置顶层 oauthAccount：账户编号与现值相同时不写文件；没有 oauthAccount 时补上；换了账户时整段换成新账户的三项。
    /// 其余字节原样、原子替换保 ACL，写完同步本代缓存。私有配置损坏或读不到时抛 <see cref="Core.Configuration.ClaudeConfigurationUnavailableException"/>。
    /// </summary>
    void RecordAccount(string accountId, Core.Upstream.OfficialAccountProfile profile);

    /// <summary>为本次隔离回环诊断准备设备编号；目录必须属于工程命名的临时诊断目录。</summary>
    void PrepareTransient(string configDirectory);

    /// <summary>受控启动或维护时对齐全部既有私有账户；不读取或更改凭据文件。</summary>
    void PrepareExistingAccounts(CancellationToken cancellation = default);
}
