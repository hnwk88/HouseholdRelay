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

    /// <summary><paramref name="accountId"/> 目录里有可用的凭据；只读文件，不刷新、不等待。</summary>
    bool HasCredential(string accountId);

    /// <summary>读取 <paramref name="accountId"/> 目录里最新的访问令牌；快到期时让 CLI 刷新，已过期时等刷新结束（有上限）再读。没有凭据时返回 null。</summary>
    string? ReadAccessToken(string accountId);

    /// <summary>
    /// 导出即搬走：这些账户此后不再续期（正在跑的续期先等它结束），按顺序读出各自的 <c>.credentials.json</c> 原文交给 <paramref name="commit"/>（加密打包、在库里记为已导出），返回它的结果；
    /// 读出的字节在返回后清零，<paramref name="commit"/> 要用就当场用完。凭据不在、续期迟迟不结束或 <paramref name="commit"/> 抛出时恢复续期并抛出（读出的原因不含文件内容）。
    /// 账户目录不删，等管理员按「删除」。
    /// </summary>
    T Export<T>(IReadOnlyList<string> accountIds, Func<IReadOnlyList<byte[]>, T> commit);

    /// <summary>删除 <paramref name="accountId"/> 的配置目录（上游被删除时调用）：先结束该账户正在跑的刷新（有上限）再删，之后不再刷新它；目录不存在或删不掉时不抛出，残留的只是一个不再被引用的目录。</summary>
    void Remove(string accountId);
}
