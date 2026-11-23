using Application.Interfaces.Siyu;
using Core.Siyu;

namespace Infrastructure.ExternalSources.Siyu;

/// <summary>
/// 服务端单飞补发现的出站实现：模板是客户端真实发出的 <c>GET /v1/mcp_servers</c> 请求头，只把 Authorization 换成官方令牌后经思域官方出口发出，
/// 读回 200 的清单（有界）、解码、取编号。不是 200、定界不合、读不懂、断开都返回 null；不重试、不跟随跳转。每次各用一条出站连接，用完即关。
/// </summary>
internal sealed class McpDiscoveryProbe : IMcpDiscoveryProbe
{
    private readonly IOfficialConnector _connector;

    /// <summary>官方出口必填。</summary>
    public McpDiscoveryProbe(IOfficialConnector connector)
    {
        ArgumentNullException.ThrowIfNull(connector);
        _connector = connector;
    }

    /// <summary>重发一次发现；成功返回编号，失败返回 null。</summary>
    public async Task<IReadOnlyList<string>?> DiscoverAsync(byte[] requestTemplate, string accessToken, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(requestTemplate); ArgumentException.ThrowIfNullOrEmpty(accessToken);
        Http1RequestHead head;
        try { head = Http1RequestHead.Parse(requestTemplate, 0); }
        // 模板来自已核对过的真实请求，解析不了说明内存数据有问题：不发。
        catch (Http1Rejection) { return null; }
        if (head.Method != "GET" || !SiyuRoutes.TryMatch(OfficialHost.Api, head.Method, head.Target, out var route) || route.Purpose != SiyuPurpose.McpServers) return null;
        try
        {
            await using var upstream = await _connector.OpenAsync(OfficialHost.Api, cancellation);
            await upstream.WriteAsync(head.WithAuthorization(accessToken), cancellation);
            await upstream.FlushAsync(cancellation);
            var reader = new Http1Reader(upstream);
            while (true)
            {
                var raw = await reader.ReadHeadAsync(cancellation);
                if (raw is null) return null;
                var response = Http1ResponseHead.Parse(raw, head.Method);
                if (response.Interim) continue;
                if (response.Status != 200) return null;
                var wire = await Http1BodyBuffer.ReadAsync(reader, response, cancellation);
                return Http1Entity.Decode(wire, response.Framing, response.Fields) is { } entity && McpServerList.TryReadIds(entity, out var ids) ? ids : null;
            }
        }
        // 官方回应不合合同：这次补不到。
        catch (Http1Rejection) { return null; }
        // 连不上、中途断开：这次补不到（超时与取消照常向上抛，由调用方处理）。
        catch (Exception error) when (error is IOException or HttpRequestException or System.Security.Authentication.AuthenticationException) { return null; }
    }
}
