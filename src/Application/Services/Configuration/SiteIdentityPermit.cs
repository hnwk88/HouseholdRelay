namespace Application.Services.Configuration;

/// <summary>一个官方操作的独立许可，重复释放只归还一次计数。</summary>
internal sealed class SiteIdentityPermit(Action release) : IDisposable
{
    private Action? remaining = release;
    /// <summary>完整响应或子进程结束后归还官方操作计数。</summary>
    public void Dispose() => Interlocked.Exchange(ref remaining, null)?.Invoke();
}
