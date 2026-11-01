namespace Application.Interfaces.ClaudeCode;

/// <summary>
/// 让中转所在电脑上的 Claude Code 跟上家里的客户端：发往官方账户的请求里看到更新的客户端版本时，在后台用本机安装方式自带的 <c>claude update</c> 升一次；
/// 同一时刻只跑一次，失败后隔一段时间才再自动试；客户端更旧时不降级，只读出提示。不碰官方账户目录和刷新令牌。
/// </summary>
public interface IClaudeCodeUpdater
{
    /// <summary>
    /// 记下一次发往官方账户的请求：使用者主键 <paramref name="clientId"/> 那台客户端自报的版本 <paramref name="clientVersion"/>，以及这次所用统一身份的版本 <paramref name="relayVersion"/>。
    /// 客户端更新时在后台起一次升级，立即返回，不等它。
    /// </summary>
    void Observe(string? clientVersion, string relayVersion, string clientId);

    /// <summary>管理员按「升级中转的 Claude Code」：不论间隔立即升一次（已在升级时等那一次），返回读给管理员的结果句。</summary>
    Task<string> Update(CancellationToken cancellation);

    /// <summary>中转版本、最近一次看到的客户端版本、两边谁新、上次升级的时间与结果，读成一段话；<paramref name="displayName"/> 按使用者主键给出显示名。不启动 Claude Code。</summary>
    string Describe(Func<string, string> displayName);
}
