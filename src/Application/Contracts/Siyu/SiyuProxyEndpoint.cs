namespace Application.Contracts.Siyu;

/// <summary>思域代理入口对客户端公布的地址（https、站点主机名、代理端口，不含凭据），由启动接线按实际监听登记；控制面原样返回，MyCoAI 不写死端口。</summary>
public sealed record SiyuProxyEndpoint(Uri Address);
