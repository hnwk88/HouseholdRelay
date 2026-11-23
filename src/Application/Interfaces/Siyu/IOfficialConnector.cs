using Core.Siyu;

namespace Application.Interfaces.Siyu;

/// <summary>思域向官方主机建出站连接：沿用站点既有出口（全局代理、地址校验），TLS 按公开证书正常验证、ALPN 只报 http/1.1。返回的流由调用方持有并释放。</summary>
public interface IOfficialConnector
{
    /// <summary>连到 <paramref name="host"/>:443 并完成 TLS 握手。</summary>
    Task<Stream> OpenAsync(OfficialHost host, CancellationToken cancellation);
}
