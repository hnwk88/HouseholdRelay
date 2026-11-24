namespace Application.Contracts.Siyu;

/// <summary>
/// 思域控制面对客户端公布的两个地址，由组合根按对外地址算一次，MyCoAI 不写死端口：<see cref="Proxy"/> 是思域代理入口（https、站点主机名、代理端口，不含凭据），官方模式用；
/// <see cref="StandardApi"/> 是站点自己的标准 API 根地址（旧接法那一条），标准模式用。
/// </summary>
public sealed record SiyuEndpoints(Uri Proxy, Uri StandardApi);
