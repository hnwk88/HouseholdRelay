using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using HouseholdRelay;
using Microsoft.AspNetCore.Builder;

sealed class TestHome : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "household-relay-foundation-" + Guid.NewGuid().ToString("N"));
    public TestHome() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        var absolute = System.IO.Path.GetFullPath(Path);
        Tests.Check(System.IO.Path.GetDirectoryName(absolute) == System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())));
        Tests.Check(System.IO.Path.GetFileName(absolute).StartsWith("household-relay-foundation-", StringComparison.Ordinal));
        // 只删除本测试独有且已验证在临时根下的目录。
        Directory.Delete(absolute, true);
    }
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
        using var certificate = Management.CreateCertificate("localhost", out var ca);
        WindowsSecrets.Save(System.IO.Path.Combine(state, "tls.dpapi"), certificate.Export(X509ContentType.Pfx));
        File.WriteAllText(System.IO.Path.Combine(state, "relay-ca.pem"), ca);
        return config;
    }
}

sealed class WebFixture : IAsyncDisposable
{
    public TestHome Home { get; } = new();
    public RelayDatabase Database { get; }
    public IdentityService Identities { get; }
    public AdministratorService Administrators { get; }
    public RelayServer Relay { get; }
    public WebApplication App { get; }
    public HttpClient Http { get; }
    public string Url => App.Urls.Single();
    public const string Password = "fixture-only-password-abc123";
    private WebFixture()
    {
        Database = new RelayDatabase(Home.Path); Identities = new(Database); Administrators = new(Database);
        Relay = new RelayServer(new ConfigurationService(Database).Read().Value, null, clients: Identities.Clients);
        App = ManagementWeb.Build(Database, Identities, Relay, 0);
        Http = NewClient();
    }
    public static HttpClient NewClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
    public static async Task<WebFixture> Create()
    { var fixture = new WebFixture(); await fixture.App.StartAsync(); return fixture; }
    public async Task<string> Csrf(string path, HttpClient? client = null)
    {
        using var response = await (client ?? Http).GetAsync(Url + path);
        Tests.Check(response.StatusCode == HttpStatusCode.OK, $"Cannot open form {path}: {response.StatusCode}");
        var html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Tests.Check(match.Success, "Antiforgery field missing"); return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
    public async Task<HttpResponseMessage> Post(string path, Dictionary<string, string> values, HttpClient? client = null, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url + path) { Content = new FormUrlEncodedContent(values) };
        request.Headers.TryAddWithoutValidation("Origin", origin ?? Url);
        return await (client ?? Http).SendAsync(request);
    }
    public async Task Login()
    {
        if (!Administrators.IsInitialized) Administrators.Initialize(Administrators.BootstrapCode()!, Password);
        using var response = await Post("/Login", new() { ["Password"] = Password, ["__RequestVerificationToken"] = await Csrf("/Login") });
        Tests.Check(response.StatusCode == HttpStatusCode.Redirect, "Login failed");
    }
    public async ValueTask DisposeAsync()
    { Http.Dispose(); await App.StopAsync(); await App.DisposeAsync(); Relay.Dispose(); Home.Dispose(); }
}

sealed class PausedUpload : HttpContent
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public PausedUpload() => Headers.ContentType = new MediaTypeHeaderValue("application/json");
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes("{")); await stream.FlushAsync(); Started.TrySetResult();
        await Continue.Task;
        await stream.WriteAsync(Encoding.UTF8.GetBytes("\"model\":\"fixture\",\"messages\":[]}"));
    }
}
