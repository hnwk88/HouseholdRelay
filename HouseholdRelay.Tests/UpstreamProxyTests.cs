using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using HouseholdRelay;
using static Tests;

/// <summary>实际 HTTP CONNECT 代理验证；目标通过 IP 建连且保持原协议路径。</summary>
static class UpstreamProxyTests
{
    public static async Task ConnectProxy()
    {
        await using var mock = await UpstreamFixture.Create(); await using var proxy = new ProxyFixture(new Uri(mock.Url).Port);
        using var home = new TestHome(); var service = new UpstreamService(new RelayDatabase(home.Path));
        var id = service.SaveCandidate(service.ReadActive().Version, mock.Options("/prefix") with { ProxyUrl = proxy.Url }, "fixture-proxy-secret");
        var report = await service.TestCandidate(id, true, CancellationToken.None);
        Check(report.CanActivate && proxy.Requests.Count > 0 && mock.Calls.Count == 2);
        Check(proxy.Requests.All(value => value.StartsWith("CONNECT 127.0.0.1:", StringComparison.Ordinal) && !value.Contains("fixture-proxy-secret")));
        Check(mock.Calls.All(call => call.Path.StartsWith("/prefix/v1/", StringComparison.Ordinal) && call.Headers["x-api-key"] == "fixture-proxy-secret"));
        var denied = service.SaveCandidate(service.ReadActive().Version, mock.Options() with { ProxyUrl = "http://169.254.169.254:8080" }, "fixture-proxy-secret");
        Check(!(await service.TestCandidate(denied, true, CancellationToken.None)).CanActivate);
    }
}

sealed class ProxyFixture : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentBag<Task> connections = [];
    private readonly Task accept;
    public ConcurrentQueue<string> Requests { get; } = new();
    public string Url => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
    public ProxyFixture(int destination) { listener.Start(); accept = Accept(destination); }
    private async Task Accept(int destination)
    {
        try { while (!stop.IsCancellationRequested) connections.Add(Serve(await listener.AcceptTcpClientAsync(stop.Token), destination)); }
        catch (OperationCanceledException) { }
    }
    private async Task Serve(TcpClient incoming, int destination)
    {
        using (incoming)
        using (var outgoing = new TcpClient())
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
                await Task.WhenAny(a, b); cancel.Cancel();
                try { await Task.WhenAll(a, b); } catch (OperationCanceledException) { }
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
        }
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await accept; await Task.WhenAll(connections); stop.Dispose(); }
}
