using System.Net.Security;
using Application.Interfaces.Siyu;
using Application.Interfaces.Upstream;
using Core.Siyu;
using Infrastructure.ExternalSources.Upstream;

namespace Infrastructure.ExternalSources.Siyu;

/// <summary>
/// 思域的官方出口：经 <see cref="UpstreamDialer.OpenTarget"/> 走站点既有出口（全局代理、地址只许公网），再按系统信任完成 TLS；
/// SNI 是官方主机名，ALPN 只报 http/1.1（内层定界只懂 HTTP/1.1），对方选了别的协议就断开。建连与握手合计限时 <see cref="OpenTimeout"/>，与代理入口的握手上限同为 15 秒。
/// </summary>
internal sealed class OfficialConnector : IOfficialConnector
{
    /// <summary>建连加握手的上限。</summary>
    internal static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);

    private readonly IUpstreamProxy _proxy;

    /// <summary><paramref name="proxy"/> 是站点全局代理设置，必填。</summary>
    public OfficialConnector(IUpstreamProxy proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        _proxy = proxy;
    }

    /// <summary>连到 <paramref name="host"/>:443 并完成 TLS 握手；失败时已打开的连接先释放再抛出。</summary>
    public async Task<Stream> Open(OfficialHost host, CancellationToken cancellation)
    {
        var name = OfficialTargets.Name(host);
        // 调用方取消（会话关闭、客户端断开）与建连上限合成一个：任一触发都立即放弃，不留半开的出站连接。
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(OpenTimeout);
        var network = await UpstreamDialer.OpenTarget(name, OfficialTargets.Port, plain: false, allowPrivate: false, _proxy, limit.Token);
        var tls = new SslStream(network, leaveInnerStreamOpen: false);
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = name,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
            }, limit.Token);
            // 对方没选 ALPN 时按 HTTP/1.1 说话（RFC 7301 允许）；选了别的协议，内层定界看不懂，不能硬转。
            if (tls.NegotiatedApplicationProtocol != default && tls.NegotiatedApplicationProtocol != SslApplicationProtocol.Http11)
                throw new IOException("官方主机没有按 HTTP/1.1 协商。");
            return tls;
        }
        // 握手失败、协议不符或超时：释放 TLS 流（连同底层连接）后原样抛出，调用方按出站失败处理。
        catch { await tls.DisposeAsync(); throw; }
    }
}
