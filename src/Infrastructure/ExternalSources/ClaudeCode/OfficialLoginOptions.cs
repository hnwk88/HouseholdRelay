namespace Infrastructure.ExternalSources.ClaudeCode;

/// <summary>官方账户登录进程的启动参数，由依赖注入以单例提供；正式服务用默认值，测试改成不出窗口、限时更短。</summary>
public sealed class OfficialLoginOptions
{
    /// <summary>
    /// 为 true（默认）时照 MyCoAI 以最小化的可见控制台运行 <c>claude auth login</c>：CLI 要认得出交互终端才会打开浏览器或打印备用登录地址，
    /// 最小化不抢前台焦点、不打断读屏，窗口仍在任务栏，打不开浏览器时还能点开看到地址。为 false 时不建窗口，只供测试使用。
    /// </summary>
    public bool ShowWindow { get; set; } = true;

    /// <summary>等管理员在浏览器里完成登录的最长时间，默认 10 分钟；超时结束登录进程并读出原因。</summary>
    public TimeSpan LoginTimeout { get; set; } = TimeSpan.FromMinutes(10);
}
