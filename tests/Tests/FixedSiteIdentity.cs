using Application.Interfaces.Configuration;
using Core.Configuration;

namespace Tests;

/// <summary>不连库的固定站点身份：设备编号固定、永远可用、许可不计数；只给直接构造探测器的单元用例用，维护操作一律不支持。</summary>
sealed class FixedSiteIdentity(string deviceId) : ISiteIdentityService
{
    /// <summary>固定的当前身份，代次 1，没有待完成维护。</summary>
    public SiteIdentitySnapshot Current { get; } = new(deviceId, 1, null, false);
    /// <summary>永远允许新的官方操作。</summary>
    public bool Available => true;
    /// <summary>固定读句。</summary>
    public string Status => "固定站点身份（测试）。";
    /// <summary>许可不计数，释放什么也不做。</summary>
    public IDisposable Enter() => new Permit();
    /// <summary>永不维护，传输停止信号永不触发。</summary>
    public CancellationToken TransportStopping => CancellationToken.None;
    /// <summary>没有私有配置要准备。</summary>
    public Task Prepare(CancellationToken cancellation) => Task.CompletedTask;
    /// <summary>不支持维护。</summary>
    public Task<SiteIdentityOperationResult> Refresh(string operationId, long expectedVersion, CancellationToken cancellation) => throw new NotSupportedException();
    /// <summary>不支持维护。</summary>
    public Task<SiteIdentityOperationResult> Resume(string operationId, CancellationToken cancellation) => throw new NotSupportedException();

    /// <summary>空许可。</summary>
    private sealed class Permit : IDisposable
    {
        /// <summary>什么也不做。</summary>
        public void Dispose() { }
    }
}
