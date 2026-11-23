namespace Application.Interfaces.Upstream;

/// <summary>
/// 委托本机 Claude Code 登录官方账户：每个登录得到的上游有自己独立的 Claude 配置目录（按上游主键命名，位于受保护的数据目录里），
/// 不读不写用户自己的 <c>%USERPROFILE%\.claude</c>；令牌由 CLI 写入和刷新，中转只读最新值。
/// </summary>
public interface IOfficialAccountLogin
{
    /// <summary>
    /// 在 <paramref name="accountId"/> 的配置目录里运行 <c>claude auth login</c>，等管理员在浏览器里登录完成，再用 <c>auth status --json</c> 取登录邮箱返回（取不到时为 null）。
    /// 未安装 Claude Code、登录被取消或失败、超时、结束后没有凭据、代理启用却取不到时抛出 <see cref="InvalidOperationException"/>（消息是读给管理员的原因与下一步），并删掉这个目录；
    /// <paramref name="cancellation"/> 取消时结束登录进程。
    /// </summary>
    Task<string?> Login(string accountId, CancellationToken cancellation);

    /// <summary>
    /// 把别处 Claude Code 登录成功后生成的 <c>.credentials.json</c>（<paramref name="content"/>，原样保存不改写）放进 <paramref name="accountId"/> 的配置目录，再用 <c>auth status --json</c> 取登录邮箱返回（取不到时为 null）。
    /// 先在内存里校验（上限 64 KB、是 JSON、有访问令牌与刷新令牌），合格才建目录；未安装 Claude Code（续期要靠它）、代理启用却取不到时同样拒绝。
    /// 失败时抛出 <see cref="InvalidOperationException"/>（消息只说缺了什么与下一步，不含文件内容）并删掉这个目录。
    /// </summary>
    Task<string?> Import(string accountId, byte[] content, CancellationToken cancellation);

    /// <summary>
    /// 重新登录换凭据：把 <paramref name="fromAccountId"/>（刚用 <see cref="Login"/> 在临时目录里登录得到的）的凭据文件换进 <paramref name="accountId"/> 的账户目录；
    /// 私有配置（设备编号、账户资料）不动。换的时候不起刷新、先等正在跑的刷新结束；已删除或搬走、等不完刷新时抛出 <see cref="InvalidOperationException"/>，原凭据不动。
    /// </summary>
    Task Adopt(string accountId, string fromAccountId);

    /// <summary><paramref name="accountId"/> 目录里有可用的凭据；只读文件，不刷新、不等待。</summary>
    bool HasCredential(string accountId);

    /// <summary>读取 <paramref name="accountId"/> 目录里最新的访问令牌；快到期时让 CLI 刷新，已过期时等刷新结束（有上限）再读。没有凭据时返回 null。</summary>
    string? ReadAccessToken(string accountId);

    /// <summary>
    /// 思域发送用：取一份确实有效的官方凭据。离到期还远就直接用；快到期时照旧让 CLI 在后台续期（每账户单飞）并用当前令牌；
    /// 已过期就等那次续期结束（有上限）再重读。重读后仍过期、没有凭据、文件不写真实到期时刻、账户已删除或搬走，一律抛 <see cref="Core.Siyu.SiyuRefusal"/>，绝不退回旧令牌。
    /// <paramref name="cancellation"/> 只取消本调用者的等待，不取消别的请求还在用的那次续期。
    /// </summary>
    Task<Core.Siyu.OfficialCredentialLease> AcquireCredential(string accountId, CancellationToken cancellation);

    /// <summary>
    /// 思域启动资料：账户私有配置的 oauthAccount 原字节与凭据文件的 claudeAiOauth，按 <see cref="Core.Siyu.SiyuLaunchProfileContract"/> 核对；不读缓存、不触发续期。
    /// 不合表、账户已删除或搬走、私有配置不可用时抛 <see cref="Core.Siyu.SiyuRefusal"/>。
    /// </summary>
    Core.Siyu.SiyuLaunchProfile ReadLaunchProfile(string accountId);

    /// <summary>读 <paramref name="accountId"/> 的 oauthAccount.accountUuid（发往官方时对齐账户段用）；已删除或搬走的账户返回 null，缺失或格式不对时按本地身份阻断抛出。不读令牌、不触发续期。</summary>
    string? ReadAccountUuid(string accountId);

    /// <summary>把获取模型或测试时向官方取到的账户资料存进 <paramref name="accountId"/> 的私有配置（见 <see cref="ClaudeCode.IClaudeConfigurationPreparation.RecordAccount"/>）；已删除或搬走的账户不写，抛 <see cref="InvalidOperationException"/>。</summary>
    void RecordAccount(string accountId, Core.Upstream.OfficialAccountProfile profile);

    /// <summary>
    /// 导出即搬走：这些账户此后不再续期（正在跑的续期先等它结束），按顺序读出各自的 <c>.credentials.json</c> 原文交给 <paramref name="commit"/>（加密打包、在库里记为已导出），返回它的结果；
    /// 读出的字节在返回后清零，<paramref name="commit"/> 要用就当场用完。凭据不在、续期迟迟不结束或 <paramref name="commit"/> 抛出时恢复续期并抛出（读出的原因不含文件内容）。
    /// 账户目录不删，等管理员按「删除」。
    /// </summary>
    Task<T> Export<T>(IReadOnlyList<string> accountIds, Func<IReadOnlyList<byte[]>, Task<T>> commit);

    /// <summary>删除 <paramref name="accountId"/> 的配置目录（上游被删除时调用）：先结束该账户正在跑的刷新（有上限）再删，之后不再刷新它；目录不存在或删不掉时不抛出，残留的只是一个不再被引用的目录。</summary>
    void Remove(string accountId);
}
