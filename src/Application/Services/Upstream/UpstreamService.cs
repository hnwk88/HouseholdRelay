using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Contracts.Upstream;
using Application.Interfaces.Security;
using Application.Interfaces.Upstream;
using Core.Configuration;
using Core.Upstream;

namespace Application.Services.Upstream;

/// <summary>网页和 CLI 共用的上游保存、获取模型、诊断、改名和原子切换服务。</summary>
public sealed class UpstreamService(IUpstreamRepository repository, ISecretProtector protector, IUpstreamProbe probe, IOfficialAccountLogin officialAccounts) : IUpstreamService
{
    /// <summary>
    /// 读取活动上游组成快照（含关联的已保存上游主键与名称）：官方登录上游读它配置目录里的最新令牌，其余解密库中凭据；
    /// 活动行缺失或配置 JSON 损坏时抛出 <see cref="InvalidOperationException"/>，未配置凭据（或登录凭据已不在）时快照的凭据为 null。
    /// </summary>
    public UpstreamSnapshot ReadActive()
    {
        var row = repository.ReadActive() ?? throw new InvalidOperationException("上游配置缺失。");
        var options = Parse<UpstreamOptions>(row.OptionsJson);
        var credential = row.ProtectedCredential is null ? null
            : options.OfficialLogin ? row.CandidateId is { } account ? officialAccounts.ReadAccessToken(account) : null : Decrypt(row.ProtectedCredential);
        return new(row.Version, options, credential, row.ReportJson is null ? null : Parse<UpstreamReport>(row.ReportJson), row.CandidateId, row.Name);
    }

    /// <summary>读取最近创建的上游（不含凭据）；一个都没有时返回 null。</summary>
    public UpstreamCandidate? LatestCandidate()
    {
        var id = repository.LatestCandidateId();
        return id is null ? null : ReadCandidate(id);
    }

    /// <summary>按创建先后列出全部已保存上游的非秘密部分。</summary>
    public IReadOnlyList<UpstreamCandidate> ListCandidates() => repository.ListCandidates().Select(ToCandidate).ToArray();

    /// <summary>按主键读取已保存上游的非秘密部分（名称、基于的活动版本、配置、状态与检测报告）；不存在时抛出 <see cref="InvalidOperationException"/> 提示重新加载。</summary>
    public UpstreamCandidate ReadCandidate(string id) => ToCandidate(repository.ReadCandidate(id) ?? throw new InvalidOperationException("这个上游不存在，请重新加载页面。"));

    /// <summary>
    /// 校验配置、名称与凭据后加密保存为未测试上游，返回主键：凭据须为 1–8192 个字符且不含空白和控制字符；
    /// 凭据长度不少于 8 时，若出现在配置 JSON、名称或（转义后）出现在地址中则拒绝，防止秘密被写进会明文展示的字段。
    /// <paramref name="name"/> 为空时用地址主机名；活动版本已不是 <paramref name="expectedVersion"/> 时由仓储拒绝并提示重新加载。
    /// </summary>
    public string SaveCandidate(long expectedVersion, UpstreamOptions options, string credential, string? name = null)
    {
        options = options.Validated();
        if (options.OfficialLogin) throw new InvalidOperationException("官方账户请按「登录官方账户」接入，不用贴令牌。");
        name = name is null ? new Uri(options.BaseUrl).Host : ValidName(name);
        if (credential.Length is < 1 or > 8192 || credential.Any(char.IsControl) || credential.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("请填写有效的上游凭据，不能含空白或控制字符。");
        var json = JsonSerializer.Serialize(options, ConfigurationFiles.Json);
        if (credential.Length >= 8 && (json.Contains(credential, StringComparison.Ordinal) || name.Contains(credential, StringComparison.Ordinal)
            || options.BaseUrl.Contains(Uri.EscapeDataString(credential), StringComparison.Ordinal)))
            throw new InvalidOperationException("凭据只能填写在密钥框里，不能写进名称、地址或模型名称。");
        var plain = Encoding.UTF8.GetBytes(credential);
        try
        {
            var encrypted = protector.Protect(plain); var id = Guid.NewGuid().ToString("N");
            repository.InsertCandidate(id, expectedVersion, name, json, encrypted);
            return id;
        }
        // 无论加密或写库是否成功都清零凭据明文字节，避免秘密残留在内存中。
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>
    /// 先生成主键，委托 <see cref="IOfficialAccountLogin.Login"/> 在以它命名的配置目录里登录，再保存为账户模式（Bearer、官方登录）的未测试上游；
    /// 名称为空时用登录邮箱（超过 40 字截断），邮箱也取不到时叫「官方账户」。库里只存加密的空凭据占位，令牌留在登录目录里由 CLI 维护。
    /// 名称不合规则在登录前就拒绝，免得白登录一次。
    /// </summary>
    public async Task<string> LoginOfficial(long expectedVersion, UpstreamOptions options, string? name, CancellationToken cancellation)
    {
        options = (options with { Mode = "oauth", Authentication = "bearer", AuthenticationManual = false, OfficialLogin = true }).Validated();
        var chosen = string.IsNullOrWhiteSpace(name) ? null : ValidName(name);
        var id = Guid.NewGuid().ToString("N");
        var email = await officialAccounts.Login(id, cancellation);
        chosen ??= ValidName(email is { Length: > 0 } ? email.Length > 40 ? email[..40] : email : "官方账户");
        repository.InsertCandidate(id, expectedVersion, chosen, Serialize(options), protector.Protect([]));
        return id;
    }

    /// <summary>校验后改名；名称只做显示，切换、检测记录和用量都按主键关联，改名不影响它们。</summary>
    public void Rename(string id, string name) => repository.Rename(id, ValidName(name));

    /// <summary>
    /// 获取模型目录：先把该上游标记为检测中（正在检测时拒绝），按认证顺序读 <c>/v1/models</c>，不发模型调用、不花额度；
    /// 自动探测时把被接受的认证方式写回配置，手动指定时保持原值；目录与读句存为未测试报告，供页面列出下拉框。
    /// </summary>
    public async Task<ModelCatalog> DiscoverModels(string id, CancellationToken cancellation)
    {
        var candidate = ReadCandidate(id); var token = LoginToken(candidate);
        var secret = repository.BeginCandidateTest(id);
        var catalog = await probe.Discover(new(candidate.ExpectedVersion, candidate.Options, token ?? Decrypt(secret)), cancellation);
        var options = catalog.Authentication is { } accepted && !candidate.Options.AuthenticationManual ? candidate.Options with { Authentication = accepted } : candidate.Options;
        var report = new UpstreamReport(DateTimeOffset.UtcNow, catalog.Status == "failed" ? "failed" : "passed",
            catalog.Status switch { "passed" => "passed", "rejected" => "reconnect", _ => "unverified" },
            catalog.Status switch { "passed" => "passed", "unsupported" => "unsupported", _ => "failed" }, "unverified", catalog.Models, null, catalog.Message);
        repository.SaveUntested(id, Serialize(options), Serialize(report));
        return catalog;
    }

    /// <summary>选定要测试的模型：名称按 <see cref="UpstreamOptions.Validated"/> 校验；与原值相同时不改动，不同时保留模型目录、清除旧的通过状态，须重新测试才能切换。</summary>
    public void ChooseModel(string id, string model)
    {
        model = model.Trim();
        if (model.Length == 0) throw new InvalidOperationException("请先选择或填写要测试的模型。");
        var candidate = ReadCandidate(id);
        if (candidate.Options.Model == model) return;
        var options = (candidate.Options with { Model = model }).Validated();
        var report = candidate.Report is { } previous ? previous with { Call = "unverified", VerifiedModel = null, Message = $"模型已改为 {model}，尚未测试。" } : null;
        repository.SaveUntested(id, Serialize(options), report is null ? null : Serialize(report));
    }

    /// <summary>
    /// 对上游做一次真实的最小模型检测并保存结果：须由操作者确认可能消耗额度（网页上就是按下写明费用的测试按钮），且已选定模型；
    /// 仓储先把它标记为检测中（正在检测时拒绝重复发起），检测结束后按 <see cref="UpstreamReport.CanActivate"/> 记为通过或失败，
    /// 并写回实际采用的认证方式与检测方式（手动指定的项目由诊断保持原值）。取消令牌会传给检测过程；检测抛出异常时不会写回结果。
    /// </summary>
    public async Task<UpstreamReport> TestCandidate(string id, bool confirmed, CancellationToken cancellation)
    {
        if (!confirmed) throw new InvalidOperationException("请先确认最小模型测试可能消耗少量上游额度。");
        var candidate = ReadCandidate(id);
        if (candidate.Options.Model.Length == 0) throw new InvalidOperationException("请先选择或填写要测试的模型，再测试。");
        var token = LoginToken(candidate);
        var secret = repository.BeginCandidateTest(id);
        // 网络等待不占用写事务，也不接触当前服务的暂停和冷却状态。
        var result = await probe.Run(new(candidate.ExpectedVersion, candidate.Options, token ?? Decrypt(secret)), cancellation);
        repository.FinishCandidateTest(id, result.Report.CanActivate ? "passed" : "failed", Serialize(result.Report), Serialize(result.Used));
        return result.Report;
    }

    /// <summary>把检测通过的上游原子地切换为新的活动版本；检测结果与活动版本的核对都在仓储的同一事务内完成。</summary>
    public void Activate(string id, long? expectedVersion = null) => repository.Activate(id, expectedVersion);

    /// <summary>校验供应商名称：去掉首尾空白后须为 1–40 个字符且不含控制字符，否则抛出 <see cref="InvalidOperationException"/> 读出要求。</summary>
    private static string ValidName(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 40 || name.Any(char.IsControl))
            throw new InvalidOperationException("请填写供应商名称（1 到 40 个字），切换时靠它分辨是哪一家。");
        return name;
    }

    /// <summary>把库中的已保存上游行解析成可展示的对象。</summary>
    private static UpstreamCandidate ToCandidate(StoredCandidate row) => new(row.Id, row.Name, row.ExpectedVersion, Parse<UpstreamOptions>(row.OptionsJson), row.State,
        row.ReportJson is null ? null : Parse<UpstreamReport>(row.ReportJson));

    /// <summary>按统一 JSON 选项序列化配置或报告。</summary>
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, ConfigurationFiles.Json);

    /// <summary>按统一 JSON 选项反序列化存储的配置或报告；内容为 JSON null 时抛出 <see cref="InvalidOperationException"/>。</summary>
    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, ConfigurationFiles.Json) ?? throw new InvalidOperationException("上游配置损坏。");

    /// <summary>
    /// 官方登录上游在标记检测开始之前先读它配置目录里的最新令牌，取不到时抛出 <see cref="InvalidOperationException"/> 提示重新登录，上游状态不变；
    /// 其余上游返回 null，由调用方解密库中凭据。
    /// </summary>
    private string? LoginToken(UpstreamCandidate candidate) => !candidate.Options.OfficialLogin ? null
        : officialAccounts.ReadAccessToken(candidate.Id) ?? throw new InvalidOperationException("这个官方账户的登录凭据已经不在了，请重新按「登录官方账户」。");

    /// <summary>解密凭据并返回 UTF-8 字符串，解密出的字节缓冲在返回前清零。</summary>
    private string Decrypt(byte[] encrypted)
    {
        var plain = protector.Unprotect(encrypted);
        try { return Encoding.UTF8.GetString(plain); }
        // 无论解码是否成功都清零解密出的字节，缩短明文凭据在内存中的停留时间。
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
