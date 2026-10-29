using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using HouseholdRelay;
using Microsoft.AspNetCore.Builder;

namespace Tests;

sealed class TestHome : IDisposable
{
    /// <summary>本次测试独占的数据目录：位于系统临时目录下，带 household-relay-foundation- 前缀和随机名。</summary>
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "household-relay-foundation-" + Guid.NewGuid().ToString("N"));
    /// <summary>创建空的测试数据目录，模拟一台从未安装过中转的电脑。</summary>
    public TestHome() => Directory.CreateDirectory(Path);
    /// <summary>删除测试数据目录：先确认它直接位于系统临时目录下且带测试前缀，校验不通过就抛异常而不删除。</summary>
    public void Dispose()
    {
        var absolute = System.IO.Path.GetFullPath(Path);
        TestSuite.Check(System.IO.Path.GetDirectoryName(absolute) == System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())));
        TestSuite.Check(System.IO.Path.GetFileName(absolute).StartsWith("household-relay-foundation-", StringComparison.Ordinal));
        // 只删除本测试独有且已验证在临时根下的目录。
        Directory.Delete(absolute, true);
    }
    /// <summary>
    /// 在 .state 目录写出旧版（数据库之前）的安装文件：server.json（study 启用、office 停用两个令牌哈希）、
    /// DPAPI 加密的上游凭据 account.dpapi、TLS 证书 tls.dpapi 和 relay-ca.pem，供迁移测试导入；返回写入的旧配置。
    /// </summary>
    public RelayConfiguration WriteLegacy()
    {
        var state = System.IO.Path.Combine(Path, ".state"); WindowsSecrets.ProtectDirectory(state);
        var config = new RelayConfiguration
        {
            UpstreamUserAgent = "claude-cli/fixture", PublicUrl = "https://localhost:5433",
            Clients = [new("study", ConfigurationFiles.Hash("legacy-study-token")), new("office", ConfigurationFiles.Hash("legacy-office-token"), false)]
        };
        ConfigurationFiles.Write(System.IO.Path.Combine(state, "server.json"), config);
        WindowsSecrets.Save(System.IO.Path.Combine(state, "account.dpapi"), Encoding.UTF8.GetBytes("fixture-legacy-upstream-secret"));
        using var certificate = TestProcess.CertificateFactory.CreateCertificate("localhost", out var ca);
        WindowsSecrets.Save(System.IO.Path.Combine(state, "tls.dpapi"), certificate.Export(X509ContentType.Pfx));
        File.WriteAllText(System.IO.Path.Combine(state, "relay-ca.pem"), ca);
        return config;
    }
}

sealed class WebFixture : IAsyncDisposable
{
    /// <summary>网页主机使用的独立临时数据目录，夹具释放时一并删除。</summary>
    public TestHome Home { get; } = new();
    /// <summary>网页主机内部使用的中转数据库，测试可直接查询或注入触发器。</summary>
    public RelayDatabase Database { get; }
    /// <summary>网页主机内的成员身份服务，用于签发、停用、轮换令牌并核对网页操作结果。</summary>
    public IIdentityService Identities { get; }
    /// <summary>网页主机内的管理员服务，用于首次初始化、登录和会话校验。</summary>
    public IAdministratorService Administrators { get; }
    /// <summary>网页主机内的中转服务实例，释放夹具时需要单独释放。</summary>
    public RelayServer Relay { get; }
    /// <summary>网页主机内的配置服务，用于读取网页保存后的配置。</summary>
    public IConfigurationService Settings => App.Services.GetRequiredService<IConfigurationService>();
    /// <summary>网页主机内的上游管理服务，用于核对网页配置、测试和激活的上游。</summary>
    public IUpstreamService Upstreams => App.Services.GetRequiredService<IUpstreamService>();
    /// <summary>网页主机内的用量价格服务，用于核对价格表单的保存结果。</summary>
    public IUsagePricingService Pricing => App.Services.GetRequiredService<IUsagePricingService>();
    /// <summary>网页主机内的私有证书服务实现，用于核对私有 CA 与服务器证书的生命周期。</summary>
    public CertificateService Certificates => (CertificateService)App.Services.GetRequiredService<ICertificateService>();
    /// <summary>在本机随机端口运行的完整管理网页主机（包含中转）。</summary>
    public WebApplication App { get; }
    /// <summary>默认浏览器会话：带 Cookie、关闭自动重定向和系统代理，便于断言 302 跳转。</summary>
    public HttpClient Http { get; }
    /// <summary>管理网页的实际监听地址。</summary>
    public string Url => App.Urls.Single();
    /// <summary>测试专用的管理员密码，仅用于本机临时数据库。</summary>
    public const string Password = "fixture-only-password-abc123";
    /// <summary>在临时目录上构建管理网页主机（尚未启动）；传入 configure 时在读取配置后再按测试需要改写。</summary>
    private WebFixture(Action<RelayConfiguration>? configure = null)
    {
        App = ManagementWeb.Build(Home.Path, 0, configure is null ? null : services => services.AddSingleton(provider =>
        { var configuration = provider.GetRequiredService<IConfigurationService>().Read().Value; configure(configuration); return configuration; }));
        Database = App.Services.GetRequiredService<RelayDatabase>(); Identities = App.Services.GetRequiredService<IIdentityService>();
        Administrators = App.Services.GetRequiredService<IAdministratorService>(); Relay = App.Services.GetRequiredService<RelayServer>();
        Http = NewClient();
    }
    /// <summary>新建一个独立的浏览器会话（单独 Cookie），关闭自动重定向和系统代理，用于模拟另一位访问者或未登录者。</summary>
    public static HttpClient NewClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
    /// <summary>构建并启动管理网页主机，返回可直接发请求的夹具。</summary>
    public static async Task<WebFixture> Create(Action<RelayConfiguration>? configure = null)
    { var fixture = new WebFixture(configure); await fixture.App.StartAsync(); return fixture; }
    /// <summary>打开指定表单页（要求 200），从页面中取出防伪令牌字段的值并解码返回；找不到该字段即判失败。</summary>
    public async Task<string> Csrf(string path, HttpClient? client = null)
    {
        using var response = await (client ?? Http).GetAsync(Url + path);
        TestSuite.Check(response.StatusCode == HttpStatusCode.OK, $"Cannot open form {path}: {response.StatusCode}");
        var html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        TestSuite.Check(match.Success, "Antiforgery field missing"); return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
    /// <summary>以表单编码提交 POST，默认带本站 Origin；传入 origin 可模拟跨站提交。调用方负责释放响应。</summary>
    public async Task<HttpResponseMessage> Post(string path, Dictionary<string, string> values, HttpClient? client = null, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url + path) { Content = new FormUrlEncodedContent(values) };
        request.Headers.TryAddWithoutValidation("Origin", origin ?? Url);
        return await (client ?? Http).SendAsync(request);
    }
    /// <summary>如尚未初始化则用首次引导码设置测试密码，再通过登录表单登录默认会话；登录必须返回 302 跳转。</summary>
    public async Task Login()
    {
        if (!Administrators.IsInitialized) Administrators.Initialize(Administrators.BootstrapCode()!, Password);
        using var response = await Post("/Login", new() { ["Password"] = Password, ["__RequestVerificationToken"] = await Csrf("/Login") });
        TestSuite.Check(response.StatusCode == HttpStatusCode.Redirect, "Login failed");
    }
    /// <summary>释放 HTTP 客户端，停止并释放网页主机和中转服务，最后删除临时数据目录。</summary>
    public async ValueTask DisposeAsync()
    { Http.Dispose(); await App.StopAsync(); await App.DisposeAsync(); Relay.Dispose(); Home.Dispose(); }
}

sealed class PausedUpload : HttpContent
{
    /// <summary>已发出请求体第一个字节并刷新后完成，测试据此确认请求已进入上传阶段。</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>测试完成该信号后才继续发送剩余请求体，用来把上传卡在中途。</summary>
    public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>设置 JSON 内容类型，让中转按正常消息请求处理。</summary>
    public PausedUpload() => Headers.ContentType = new MediaTypeHeaderValue("application/json");
    /// <summary>声明长度未知，迫使请求以分块方式上传，使中途暂停成为可能。</summary>
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    /// <summary>先写出并刷新 "{"，发出 Started 信号，等待 Continue 后再写出其余 JSON，模拟上传途中停顿。</summary>
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes("{")); await stream.FlushAsync(); Started.TrySetResult();
        await Continue.Task;
        await stream.WriteAsync(Encoding.UTF8.GetBytes("\"model\":\"fixture\",\"messages\":[]}"));
    }
}
