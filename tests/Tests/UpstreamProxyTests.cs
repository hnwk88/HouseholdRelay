using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using HouseholdRelay;
using static Tests.TestSuite;

namespace Tests;

/// <summary>实际 HTTP CONNECT 代理验证；目标通过 IP 建连且保持原协议路径。</summary>
static class UpstreamProxyTests
{
    /// <summary>
    /// 验证全局手动代理走 HTTP CONNECT：项目设置填好代理后，上游经代理测试成功，代理收到的 CONNECT 目标是已解析校验过的 IP 而非主机名，且不含上游密钥；
    /// 上游收到的两次诊断请求保留 /prefix/v1/ 路径和 x-api-key；把全局代理改成云元数据地址 169.254.169.254 后，同一个上游再测不可激活。
    /// </summary>
    public static async Task ConnectProxy()
    {
        await using var mock = await UpstreamFixture.Create(); await using var proxy = new ProxyFixture(new Uri(mock.Url).Port);
        using var home = new TestHome(); var process = new TestProcess(home.Path); var service = process.Upstreams;
        UseProxy(process, "manual", proxy.Url);
        var id = service.SaveCandidate(service.ReadActive().Version, mock.Options("/prefix"), "fixture-proxy-secret");
        var report = await service.TestCandidate(id, true, CancellationToken.None);
        Check(report.CanActivate && proxy.Requests.Count > 0 && mock.Calls.Count == 2);
        Check(proxy.Requests.All(value => value.StartsWith("CONNECT 127.0.0.1:", StringComparison.Ordinal) && !value.Contains("fixture-proxy-secret")));
        Check(mock.Calls.All(call => call.Path.StartsWith("/prefix/v1/", StringComparison.Ordinal) && call.Headers["x-api-key"] == "fixture-proxy-secret"));
        UseProxy(process, "manual", "http://169.254.169.254:8080");
        Check(!(await service.TestCandidate(id, true, CancellationToken.None)).CanActivate);
    }

    /// <summary>经配置服务按版本保存全局代理方式与地址，与项目设置页走同一条写入路径。</summary>
    public static void UseProxy(TestProcess process, string mode, string? url)
    {
        var snapshot = process.Settings.Read(); snapshot.Value.ProxyMode = mode; snapshot.Value.ProxyUrl = url;
        process.Settings.Save(snapshot.Version, snapshot.Value);
    }
}

sealed class ProxyFixture : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentBag<Task> connections = [];
    private readonly Task accept;
    /// <summary>代理收到的每条 CONNECT 请求头原文，用于断言目标写法和不含密钥。</summary>
    public ConcurrentQueue<string> Requests { get; } = new();
    /// <summary>代理在本机回环地址上的访问地址，作为候选上游的 ProxyUrl。</summary>
    public string Url => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
    /// <summary>开始在随机端口监听，并启动后台接受循环，把隧道接到本机的目标端口。</summary>
    public ProxyFixture(int destination) { listener.Start(); accept = Accept(destination); }
    /// <summary>循环接受连接并交给 <see cref="Serve"/> 处理，直到代理停止。</summary>
    private async Task Accept(int destination)
    {
        try { while (!stop.IsCancellationRequested) connections.Add(Serve(await listener.AcceptTcpClientAsync(stop.Token), destination)); }
        // 释放代理时取消接受循环属于正常退出，不视为错误。
        catch (OperationCanceledException) { }
    }
    /// <summary>
    /// 模拟最小 HTTP CONNECT 代理：读取不超过 8192 字节的请求头并记录，断言目标为 127.0.0.1 加预期端口，
    /// 然后连到目标端口、回复 200 Connection Established，并双向复制字节直到任一方向结束。
    /// </summary>
    private async Task Serve(TcpClient incoming, int destination)
    {
        using (incoming)
        using (var outgoing = new TcpClient())
        // 每条连接用关联取消源：代理停止或任一方向结束时都能同时关闭两个方向的复制。
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            try
            {
                var header = new List<byte>(); var one = new byte[1];
                while (header.Count < 8192)
                {
                    if (await incoming.GetStream().ReadAsync(one, cancel.Token) == 0) return;
                    header.Add(one[0]); if (header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                var text = Encoding.ASCII.GetString(header.ToArray()); Requests.Enqueue(text);
                Check(text.StartsWith($"CONNECT 127.0.0.1:{destination} HTTP/1.1", StringComparison.Ordinal));
                await outgoing.ConnectAsync(IPAddress.Loopback, destination, cancel.Token);
                await incoming.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), cancel.Token);
                var a = incoming.GetStream().CopyToAsync(outgoing.GetStream(), cancel.Token);
                var b = outgoing.GetStream().CopyToAsync(incoming.GetStream(), cancel.Token);
                // 任一方向结束就取消另一方向，避免半关闭的隧道一直挂起。
                await Task.WhenAny(a, b); cancel.Cancel();
                try { await Task.WhenAll(a, b); } catch (OperationCanceledException) { /* 另一方向会因上面的取消而抛出取消异常，这是预期结果，忽略它以免把正常关闭误报为失败。 */ }
            }
            // 对端重置、代理关闭等网络异常只结束这一条隧道，不影响其他连接；断言失败抛出的普通异常不在此列，会照常暴露。
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
        }
    }
    /// <summary>先取消并停止监听，再等待接受循环和全部隧道任务结束，最后释放取消源，保证不遗留后台连接。</summary>
    // 先取消再等待：取消会同时终止接受循环和每条隧道的复制，等全部任务结束后才释放取消源，避免任务仍在使用已释放的令牌。
    public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await accept; await Task.WhenAll(connections); stop.Dispose(); }
}
