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

    /// <summary>读取 <paramref name="accountId"/> 目录里最新的访问令牌；快到期时让 CLI 刷新，已过期时等刷新结束（有上限）再读。没有凭据时返回 null。</summary>
    string? ReadAccessToken(string accountId);
}
