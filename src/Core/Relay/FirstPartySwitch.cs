namespace Core.Relay;

/// <summary>
/// 家里 Claude Code 的「按官方直连发请求」开关（<c>_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL=1</c>）。开了它，Claude Code 每个对话请求都自己生成 <c>X-Client-Request-Id</c> 等一批官方直连才有的值
/// （订单 133 回环实测 2.1.323：开关组每个请求都带，不开组一个都不带）；所以 Claude Code 发来的对话请求没带这个头，就说明这台电脑还没开开关。
/// </summary>
public static class FirstPartySwitch
{
    /// <summary>环境变量名。</summary>
    public const string Variable = "_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL";

    /// <summary>读给下游、让使用者在终端里执行一次的开关命令；执行后重开 Claude Code 再发即可。</summary>
    public const string Instruction =
        "这台电脑的 Claude Code 还没开「按官方直连发请求」开关，这次没有发往官方。请复制下面一行在 PowerShell 执行一次，再完全关掉并重开 Claude Code：\n"
        + "[Environment]::SetEnvironmentVariable('" + Variable + "', '1', 'User')";

    /// <summary>
    /// 判断这次对话请求是不是来自没开开关的客户端：只对 Claude Code（User-Agent 以 <c>claude-cli/</c> 开头）判断；它开了开关会带 <c>X-Client-Request-Id</c>，没带即视为未开。
    /// 非 Claude Code 客户端（没有这个 User-Agent）不在此列，照常放行。
    /// </summary>
    public static bool Missing(string userAgent, string clientRequestId) =>
        userAgent.StartsWith("claude-cli/", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(clientRequestId);
}
