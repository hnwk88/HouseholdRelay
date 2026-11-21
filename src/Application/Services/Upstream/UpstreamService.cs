using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Contracts.Upstream;
using Application.Contracts.Uploads;
using Application.DependencyInjection;
using Application.Interfaces.Configuration;
using Application.Interfaces.Security;
using Application.Interfaces.Upstream;
using Core.Configuration;
using Core.Upstream;
using Core.Usage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Repositories;

namespace Application.Services.Upstream;

/// <summary>
/// 网页和 CLI 共用的上游保存、获取模型、诊断、改名和原子切换服务。活动上游（upstream）与已保存上游（upstream_candidates）经通用仓储存取，每次操作一个作用域；
/// 检测状态流转、能不能删、能不能切换都由实体 <see cref="UpstreamCandidate"/>、<see cref="ActiveUpstream"/> 的方法判断，这里只做场景编排。
/// </summary>
public sealed class UpstreamService(IServiceScopeFactory scopes, ISecretProtector protector, IUpstreamProbe probe, IOfficialAccountLogin officialAccounts, IPasswordCipher cipher, ISiteIdentityService siteIdentity) : IUpstreamService
{
    /// <summary>
    /// 读取活动上游组成快照（含关联的已保存上游主键与名称）：官方登录上游读它配置目录里的最新令牌，其余解密库中凭据；
    /// 活动行缺失或配置 JSON 损坏时抛出 <see cref="InvalidOperationException"/>，未配置凭据（或登录凭据已不在）时快照的凭据为 null。
    /// 来源的官方账户已导出搬走时也为 null：不读令牌也就不触发续期，转发按「未配置」处理（自动切换开着时换到备用上游）。
    /// </summary>
    public async Task<UpstreamSnapshot> ReadActive()
    {
        var (row, source, options) = await Active();
        var (credential, accountUuid, blocked, reason) = ReadCredential(options, row.Credential, source?.ExportedAt is not null, source?.Uid);
        return new(row.Version, options, credential, Report(row.ReportJson), row.UpstreamCandidateId, Named(source?.Name), row.Failover, blocked, accountUuid, reason);
    }

    /// <summary>
    /// 读取在用那一家以外、测试通过、没有导出搬走的已保存上游快照，按上游页顺序（创建先后），版本记为此刻的活动版本：官方登录的读它账户目录里的最新令牌（取不到时凭据为 null，转发会跳过它），其余解密库中凭据。
    /// 配置或报告 JSON 损坏的那一家跳过，不挡住其余备用上游。
    /// </summary>
    public async Task<IReadOnlyList<UpstreamSnapshot>> ReadStandby()
    {
        List<UpstreamCandidate> rows; ActiveUpstream? active;
        await using (var scope = scopes.CreateAsyncScope())
        {
            active = await scope.Repository<ActiveUpstream>().GetByIdAsync(ActiveUpstream.SingletonId);
            rows = await scope.Repository<UpstreamCandidate>().FindAsync(item => item.State == CandidateState.Passed && item.ExportedAt == null);
        }
        if (active is null) return [];
        var snapshots = new List<UpstreamSnapshot>();
        foreach (var row in InPageOrder(rows).Where(item => item.Id != active.UpstreamCandidateId))
        {
            UpstreamOptions options; UpstreamReport? report;
            try { options = Parse<UpstreamOptions>(row.OptionsJson); report = Report(row.ReportJson); }
            // 一家的配置坏了只让它不参与切换，在用的那家和其余备用照常；它在上游页上照样读得出来，由管理员重新测试或删除。
            catch (Exception error) when (error is JsonException or InvalidOperationException) { continue; }
            var (credential, accountUuid, blocked, reason) = ReadCredential(options, row.Credential, false, row.Uid);
            snapshots.Add(new(active.Version, options, credential, report, row.Id, Named(row.Name), blockedByIdentity: blocked, accountUuid: accountUuid, blockedReason: reason));
        }
        return snapshots;
    }

    /// <summary>先原子取得官方许可再读取凭据；维护竞争只关闭这个官方候选，不中断后续独立 API 候选。</summary>
    private (string? Credential, string? AccountUuid, bool Blocked, string? Reason) ReadCredential(UpstreamOptions options, byte[]? protectedCredential, bool exported, string? accountId)
    {
        if (protectedCredential is null || exported) return (null, null, false, null);
        try
        {
            using var operation = options.OfficialAccount ? siteIdentity.Enter() : null;
            // 官方登录上游同时读账户编号（oauthAccount.accountUuid），用于把发往官方的账户段对齐原生直连；两者共用账户目录的代次缓存，正常转发不多开文件。取不到按本地身份阻断。
            var credential = options.OfficialLogin ? accountId is null ? null : officialAccounts.ReadAccessToken(accountId) : Decrypt(protectedCredential);
            var accountUuid = options.OfficialLogin && accountId is not null ? officialAccounts.ReadAccountUuid(accountId) : null;
            return (credential, accountUuid, false, null);
        }
        // 只接住明确的维护或私有身份配置拒绝，包含账户读取内部再次准入的竞争；凭据解密损坏或未知异常仍按原规则暴露。
        // 私有配置拒绝的读句是固定说明（如「还没有账户编号，请按测试」），带给下游；站点维护用通用读句。
        catch (Exception error) when (error is SiteIdentityUnavailableException or ClaudeConfigurationUnavailableException)
        { return (null, null, true, error is ClaudeConfigurationUnavailableException ? error.Message : null); }
    }

    /// <summary>打开或关闭自动切换；保存即生效，下一个请求就按新开关取尝试顺序，不改版本、不换在用的那一家。</summary>
    public async Task SetFailover(bool enabled)
    {
        await using var scope = scopes.CreateAsyncScope();
        var active = scope.Repository<ActiveUpstream>();
        (await active.GetByIdAsync(ActiveUpstream.SingletonId) ?? throw new InvalidOperationException("上游配置缺失。")).Failover = enabled;
        await Commit(active, "活动上游刚被另一页面或命令改过，请重新加载页面再试。");
    }

    /// <summary>
    /// 读取活动上游的非秘密部分供页面展示：不解密、不读令牌，官方登录上游的「已配置」只看账户目录里有没有凭据文件，不等刷新；
    /// 「已配置」与 <see cref="ReadActive"/> 的 <see cref="UpstreamSnapshot.Configured"/> 同一口径（已导出搬走的读作未配置）。活动行缺失或配置 JSON 损坏时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public async Task<UpstreamOverview> ReadOverview()
    {
        var (row, source, options) = await Active();
        var configured = row.Credential is not null && source?.ExportedAt is null && (!options.OfficialLogin || source?.Uid is { } account && officialAccounts.HasCredential(account));
        return new(row.Version, options, configured, Report(row.ReportJson), row.UpstreamCandidateId, Named(source?.Name), row.Failover);
    }

    /// <summary>读取最近创建的上游（不含凭据）；一个都没有时返回 null。</summary>
    public async Task<UpstreamCandidateView?> LatestCandidate() => (await ListCandidates()).LastOrDefault();

    /// <summary>按创建先后列出全部未停用的已保存上游的非秘密部分，每家带上此刻能不能删。</summary>
    public async Task<IReadOnlyList<UpstreamCandidateView>> ListCandidates()
    {
        await using var scope = scopes.CreateAsyncScope();
        var active = await ActiveCandidateId(scope);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return InPageOrder(await scope.Repository<UpstreamCandidate>().GetAllAsync()).Select(item => ToView(item, now, active)).ToArray();
    }

    /// <summary>按主键读取已保存上游的非秘密部分（名称、基于的活动版本、配置、状态与检测报告）；不存在或已停用时抛出 <see cref="InvalidOperationException"/> 提示重新加载。</summary>
    public async Task<UpstreamCandidateView> ReadCandidate(long id)
    {
        await using var scope = scopes.CreateAsyncScope();
        var row = await scope.Repository<UpstreamCandidate>().GetByIdAsync(id) ?? throw Missing();
        return ToView(row, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), await ActiveCandidateId(scope));
    }

    /// <summary>
    /// 校验配置、名称与凭据后加密保存为未测试上游，返回主键：凭据须为 1–8192 个字符且不含空白和控制字符；
    /// 凭据长度不少于 8 时，若出现在配置 JSON、名称或（转义后）出现在地址中则拒绝，防止秘密被写进会明文展示的字段。
    /// <paramref name="name"/> 为空时用地址主机名；活动版本已不是 <paramref name="expectedVersion"/> 时拒绝并提示重新加载。
    /// </summary>
    public async Task<long> SaveCandidate(long expectedVersion, UpstreamOptions options, string credential, string? name = null)
    {
        options = options.Validated();
        using var operation = options.OfficialAccount ? siteIdentity.Enter() : null;
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
            var encrypted = protector.Protect(plain);
            return await Insert(Guid.NewGuid().ToString("N"), expectedVersion, name, json, encrypted);
        }
        // 无论加密或写库是否成功都清零凭据明文字节，避免秘密残留在内存中。
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>委托 <see cref="IOfficialAccountLogin.Login"/> 在新上游自己的配置目录里登录，再按 <see cref="AddOfficial"/> 取名、保存。</summary>
    public Task<long> LoginOfficial(long expectedVersion, UpstreamOptions options, string? name, CancellationToken cancellation) =>
        AddOfficial(expectedVersion, options, name, id => officialAccounts.Login(id, cancellation));

    /// <summary>委托 <see cref="IOfficialAccountLogin.Import"/> 把登录文件放进新上游自己的配置目录，再按 <see cref="AddOfficial"/> 取名、保存；得到的上游与登录得到的不可区分。</summary>
    public Task<long> ImportOfficial(long expectedVersion, UpstreamOptions options, byte[] content, string? name, CancellationToken cancellation) =>
        AddOfficial(expectedVersion, options, name, id => officialAccounts.Import(id, content, cancellation));

    /// <summary>校验后改名；名称只做显示，切换、检测记录和用量都按主键关联，改名不影响它们。不存在或已停用时提示重新加载。</summary>
    public async Task Rename(long id, string name)
    {
        name = ValidName(name);
        await using var scope = scopes.CreateAsyncScope();
        var candidates = scope.Repository<UpstreamCandidate>();
        (await candidates.GetByIdAsync(id) ?? throw Missing()).Name = name;
        await Commit(candidates, Changed);
    }

    /// <summary>
    /// 删除一个已保存上游，做法是停用：在一个事务里读出它与活动上游，当前使用中（活动上游的来源）或正在检测时拒绝并读出下一步；
    /// 能删就清掉加密凭据并软删除，同一事务里数出仍按主键引用它的用量、尝试和单价条数返回。
    /// 停用后用量、尝试和单价照常按外键引用它，用量页读出它的名字；删掉的是官方登录上游时再删它的账户目录。
    /// </summary>
    public async Task<UpstreamReferences> Delete(long id)
    {
        await using var scope = scopes.CreateAsyncScope();
        var candidates = scope.Repository<UpstreamCandidate>();
        var (references, removed) = await candidates.ExecuteInTransactionAsync(async () =>
        {
            var candidate = await candidates.GetByIdAsync(id) ?? throw Missing();
            var active = await ActiveCandidateId(scope); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!candidate.Deletable(now, active))
                throw new InvalidOperationException(active == id ? "这个上游正在使用中，不能删除。请先切换到别的上游，再删除它。" : "这个上游正在检测，请等检测结束后再删除。");
            candidate.PrepareDeactivation(now, active);
            await candidates.SoftDeleteAsync(candidate);
            await candidates.SaveChangesAsync();
            var references = new UpstreamReferences(await scope.Repository<UsageRequest>().CountAsync(item => item.UpstreamCandidateId == id),
                await scope.Repository<UsageAttempt>().CountAsync(item => item.UpstreamCandidateId == id),
                await scope.Repository<UsagePriceRecord>().CountAsync(item => item.UpstreamCandidateId == id));
            return (references, candidate);
        });
        if (Parse<UpstreamOptions>(removed.OptionsJson).OfficialLogin) officialAccounts.Remove(removed.Uid);
        return references;
    }

    /// <summary>
    /// 获取模型目录：先把该上游标记为检测中（正在检测时拒绝），按认证顺序读 <c>/v1/models</c>，不发模型调用、不花额度；
    /// 自动探测时把被接受的认证方式写回配置，手动指定时保持原值；目录与读句存为未测试报告，供页面列出下拉框。
    /// 当前使用中的那个已经在转发：结束后（含失败、取消）写回原状态、原配置与原报告，获取成功时只把报告里的模型目录换成新的，不读成未测试。
    /// </summary>
    public async Task<ModelCatalog> DiscoverModels(long id, CancellationToken cancellation)
    {
        var candidate = await ReadCandidate(id);
        using var operation = candidate.Options.OfficialAccount ? siteIdentity.Enter() : null;
        var (token, accountUuid) = await LoginToken(candidate, cancellation);
        var kept = candidate.Report is { } previous && (await ActiveCandidateId()) == id ? previous : null;
        var secret = await BeginTest(id);
        ModelCatalog catalog;
        try { catalog = await probe.Discover(new(candidate.ExpectedVersion, candidate.Options, token ?? Decrypt(secret), accountUuid: accountUuid), cancellation); }
        // 在用的那个获取途中失败或被取消：写回原样，不让它停在「检测中」、丢掉测试结论。
        catch when (kept is not null) { await FinishTest(id, candidate.State, Serialize(kept), Serialize(candidate.Options)); throw; }
        if (kept is not null)
        {
            await FinishTest(id, candidate.State, Serialize(catalog.Status == CatalogStatus.Passed ? kept with { Models = catalog.Models } : kept), Serialize(candidate.Options));
            return catalog;
        }
        var options = catalog.Authentication is { } accepted && !candidate.Options.AuthenticationManual ? candidate.Options with { Authentication = accepted } : candidate.Options;
        var report = new UpstreamReport(DateTimeOffset.UtcNow, catalog.Status == CatalogStatus.Failed ? CheckStatus.Failed : CheckStatus.Passed,
            UpstreamDetection.AuthenticationCheck(catalog.Status), UpstreamDetection.DiscoveryCheck(catalog.Status), CheckStatus.Unverified, catalog.Models, null, catalog.Message);
        await ResetToUntested(id, Serialize(options), Serialize(report));
        return catalog;
    }

    /// <summary>选定要测试的模型：名称按 <see cref="UpstreamOptions.Validated"/> 校验；与原值相同时不改动，不同时保留模型目录、清除旧的通过状态，须重新测试才能切换。</summary>
    public async Task ChooseModel(long id, string model)
    {
        model = model.Trim();
        if (model.Length == 0) throw new InvalidOperationException("请先选择或填写要测试的模型。");
        var candidate = await ReadCandidate(id);
        if (candidate.Options.Model == model) return;
        var options = (candidate.Options with { Model = model }).Validated();
        var report = candidate.Report is { } previous ? previous with { Call = CheckStatus.Unverified, VerifiedModel = null, Message = $"模型已改为 {model}，尚未测试。" } : null;
        await ResetToUntested(id, Serialize(options), report is null ? null : Serialize(report));
    }

    /// <summary>
    /// 对上游做一次真实的最小模型检测并保存结果：须由操作者确认可能消耗额度（网页上就是按下写明费用的测试按钮），且已选定模型；
    /// 先把它标记为检测中（正在检测时拒绝重复发起），检测结束后按 <see cref="UpstreamReport.CanActivate"/> 记为通过或失败，
    /// 并写回实际采用的认证方式与检测方式（手动指定的项目由诊断保持原值）。取消令牌会传给检测过程；检测抛出异常时不会写回结果。
    /// </summary>
    public async Task<UpstreamReport> TestCandidate(long id, bool confirmed, CancellationToken cancellation)
    {
        if (!confirmed) throw new InvalidOperationException("请先确认最小模型测试可能消耗少量上游额度。");
        var candidate = await ReadCandidate(id);
        using var operation = candidate.Options.OfficialAccount ? siteIdentity.Enter() : null;
        if (candidate.Options.Model.Length == 0) throw new InvalidOperationException("请先选择或填写要测试的模型，再测试。");
        var (token, accountUuid) = await LoginToken(candidate, cancellation);
        var secret = await BeginTest(id);
        // 网络等待不占用写事务，也不接触当前服务的暂停和冷却状态。
        var result = await probe.Run(new(candidate.ExpectedVersion, candidate.Options, token ?? Decrypt(secret), accountUuid: accountUuid), cancellation);
        await FinishTest(id, result.Report.CanActivate ? CandidateState.Passed : CandidateState.Failed, Serialize(result.Report), Serialize(result.Used));
        return result.Report;
    }

    /// <summary>
    /// 把检测通过的上游原子地切换为新的活动版本：在一个事务里读出它与活动上游，由 <see cref="ActiveUpstream.Activate"/> 核对检测结果、没有导出搬走与活动版本后改写。
    /// </summary>
    public async Task Activate(long id, long? expectedVersion = null)
    {
        using var operation = (await ReadCandidate(id)).Options.OfficialAccount ? siteIdentity.Enter() : null;
        await using var scope = scopes.CreateAsyncScope();
        var active = scope.Repository<ActiveUpstream>();
        await active.ExecuteInTransactionAsync(async () =>
        {
            var candidate = await scope.Repository<UpstreamCandidate>().GetByIdAsync(id) ?? throw UpstreamCandidate.NotTested();
            var row = await active.GetByIdAsync(ActiveUpstream.SingletonId) ?? throw new InvalidOperationException("活动上游已被另一页面或命令更换，请重新加载页面后再切换。");
            row.Activate(candidate, expectedVersion);
            return await active.SaveChangesAsync();
        });
    }

    /// <summary>
    /// 把勾选的上游（按上游页顺序）加密导出：API 密钥与贴进来的账户令牌解密后放进文件，登录得到的官方账户放它账户目录里的登录文件原文；
    /// 整个文件用 <paramref name="password"/> 加密，密码不保存。官方账户经 <see cref="IOfficialAccountLogin.Export"/> 停掉续期、读出登录文件，
    /// 加密成功后在库里记为已导出，此后本实例不再用它转发、不再续期；任一步失败都不记、照常续期。明文正文用完清零。
    /// 密码不在 12–128 个字符、一家都没勾、勾的上游已不在时抛出 <see cref="InvalidOperationException"/> 读出原因。
    /// </summary>
    public async Task<UpstreamExport> Export(IReadOnlyCollection<long> ids, string password)
    {
        if (password.Length is < 12 or > 128) throw new InvalidOperationException("导出密码需要 12 至 128 个字符。导入时要输入同一个密码，请记牢。");
        var wanted = ids.ToHashSet();
        if (wanted.Count == 0) throw new InvalidOperationException("请至少勾选一家要导出的上游，再按「导出加密文件」。");
        var chosen = (await ListCandidates()).Where(item => wanted.Contains(item.Id)).ToArray();
        if (chosen.Length != wanted.Count) throw new InvalidOperationException("要导出的上游有一家已经不在了，请重新加载页面后再导出。");
        using var operation = chosen.Any(item => item.Options.OfficialAccount) ? siteIdentity.Enter() : null;
        var officials = chosen.Where(item => item.Options.OfficialLogin).ToArray();
        return await officialAccounts.Export(officials.Select(item => item.Uid).ToArray(), async files =>
        {
            var logins = officials.Select((item, index) => (item.Id, File: files[index])).ToDictionary(pair => pair.Id, pair => pair.File);
            var upstreams = new List<ExportedUpstream>();
            foreach (var item in chosen)
                upstreams.Add(item.Options.OfficialLogin ? new ExportedUpstream(item.Name, item.Options, null, logins[item.Id])
                    : new ExportedUpstream(item.Name, item.Options, Decrypt(await ReadCredential(item.Id)), null));
            var plain = JsonSerializer.SerializeToUtf8Bytes(new UpstreamArchive(upstreams.ToArray()), ConfigurationFiles.Json);
            try
            {
                var file = cipher.Seal(plain, password);
                await MarkExported(officials.Select(item => item.Id).ToArray());
                return new UpstreamExport(file, chosen.Select(item => item.Name).ToArray(), officials.Select(item => item.Name).ToArray());
            }
            // 明文正文里有全部密钥和登录文件：加密成败都清零。
            finally { CryptographicOperations.ZeroMemory(plain); }
        });
    }

    /// <summary>
    /// 用 <paramref name="password"/> 解开导出文件，按文件里的顺序逐家加成未测试上游，排在已有上游后面：API 密钥与账户令牌走 <see cref="SaveCandidate"/>，
    /// 官方账户的登录文件走 <see cref="ImportOfficial"/>（同一套校验、各自的账户目录），名称照文件。
    /// 文件超过 <see cref="UploadLimits.UpstreamExportBytes"/>、不是导出文件、格式版本不认识、密码不对、文件被改过、读不懂或活动版本已变化时抛出 <see cref="InvalidOperationException"/>，一家都不写；
    /// 解开以后某一家被拒（例如本机没装 Claude Code）只记在它自己的结果里，其余照导。解开的正文和登录文件用完清零。
    /// </summary>
    public async Task<IReadOnlyList<UpstreamImportOutcome>> Import(long expectedVersion, byte[] file, string password, CancellationToken cancellation)
    {
        if (file.Length > UploadLimits.UpstreamExportBytes) throw new InvalidOperationException("选的文件超过 1 MB，不是上游导出文件。请选旧电脑上按「导出加密文件」下载的那个文件。");
        if (password.Length is 0 or > 128) throw new InvalidOperationException("请输入导出时设的那个密码（12 至 128 个字符），再按「导入上游」。");
        var plain = cipher.Open(file, password);
        UpstreamArchive? archive;
        try { archive = JsonSerializer.Deserialize<UpstreamArchive>(plain, ConfigurationFiles.Json); }
        // 密码对、校验也过了却读不懂：只会是两边版本不一样；解析错误的消息里可能带正文片段，只读出结论。
        catch (JsonException) { archive = null; }
        // 解开的正文里有全部密钥和登录文件：读完即清零。
        finally { CryptographicOperations.ZeroMemory(plain); }
        try
        {
            if (archive?.Upstreams is not { Count: > 0 } upstreams || upstreams.Any(item => item?.Options is null))
                throw new InvalidOperationException("导出文件解开了，但里面的上游读不懂或是空的。请把两边的中转升级到同一版本，再导出、导入一次。");
            if ((await ActiveVersion()) != expectedVersion) throw new InvalidOperationException("活动配置已改变，请重新加载后再导入。");
            var outcomes = new List<UpstreamImportOutcome>();
            foreach (var item in upstreams)
            {
                var name = string.IsNullOrWhiteSpace(item.Name) ? "没有名称的上游" : item.Name;
                try
                {
                    var id = item.Options.OfficialLogin
                        ? await ImportOfficial(expectedVersion, item.Options, item.LoginFile ?? throw new InvalidOperationException("文件里缺少这个官方账户的登录文件。"), item.Name, cancellation)
                        : await SaveCandidate(expectedVersion, item.Options, item.Credential ?? throw new InvalidOperationException("文件里缺少这家上游的密钥。"), item.Name);
                    outcomes.Add(new(name, id, null));
                }
                // 这一家被拒（名称、地址不合规则，没装 Claude Code，代理取不到等）：记下原因，其余照导。
                catch (InvalidOperationException error) { outcomes.Add(new(name, null, error.Message)); }
            }
            return outcomes;
        }
        // 登录文件里有访问令牌和刷新令牌：放进账户目录以后无论成败都清零。
        finally { foreach (var item in archive?.Upstreams ?? []) if (item?.LoginFile is { } login) CryptographicOperations.ZeroMemory(login); }
    }

    /// <summary>校验供应商名称：去掉首尾空白后须为 1–40 个字符且不含控制字符，否则抛出 <see cref="InvalidOperationException"/> 读出要求。</summary>
    private static string ValidName(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 40 || name.Any(char.IsControl))
            throw new InvalidOperationException("请填写供应商名称（1 到 40 个字），切换时靠它分辨是哪一家。");
        return name;
    }

    /// <summary>
    /// 登录与导入共用的尾段：先校验配置与名称（不合规则时在拿凭据之前就拒绝），生成对外编号（<see cref="UpstreamCandidateView.Uid"/>）交给 <paramref name="acquire"/> 把凭据放进以它命名的配置目录，
    /// 再保存为账户模式（Bearer、官方登录）的未测试上游，返回库生成的主键。名称为空时用登录邮箱（超过 40 字截断），邮箱也取不到时叫「官方账户」；
    /// 库里只存加密的空凭据占位，令牌留在账户目录里由 CLI 维护。写库被拒（活动版本已变化）时删掉刚放好的账户目录。
    /// </summary>
    private async Task<long> AddOfficial(long expectedVersion, UpstreamOptions options, string? name, Func<string, Task<string?>> acquire)
    {
        options = (options with { Mode = UpstreamMode.OAuth, Authentication = AuthenticationScheme.Bearer, AuthenticationManual = false, OfficialLogin = true }).Validated();
        using var operation = siteIdentity.Enter();
        var chosen = string.IsNullOrWhiteSpace(name) ? null : ValidName(name);
        var uid = Guid.NewGuid().ToString("N");
        var email = await acquire(uid);
        try
        {
            chosen ??= ValidName(email is { Length: > 0 } ? email.Length > 40 ? email[..40] : email : "官方账户");
            return await Insert(uid, expectedVersion, chosen, Serialize(options), protector.Protect([]));
        }
        // 凭据已在账户目录里、上游却没建成：删掉目录，不留没有上游引用的账户；异常原样读出。
        catch { officialAccounts.Remove(uid); throw; }
    }

    /// <summary>在一个事务里核对活动版本仍是 <paramref name="expectedVersion"/>，写入带名称与对外编号的未测试上游，返回库生成的主键。</summary>
    private async Task<long> Insert(string uid, long expectedVersion, string name, string optionsJson, byte[] protectedCredential)
    {
        await using var scope = scopes.CreateAsyncScope();
        var candidates = scope.Repository<UpstreamCandidate>();
        return await candidates.ExecuteInTransactionAsync(async () =>
        {
            var active = await scope.Repository<ActiveUpstream>().GetByIdAsync(ActiveUpstream.SingletonId);
            if (active is null) throw new InvalidOperationException("活动配置已改变，请重新加载后再保存。");
            active.EnsureVersion(expectedVersion, "活动配置已改变，请重新加载后再保存。");
            var candidate = new UpstreamCandidate { Uid = uid, ExpectedVersion = expectedVersion, OptionsJson = optionsJson, Credential = protectedCredential, State = CandidateState.Untested, Name = name };
            await candidates.AddAsync(candidate);
            await candidates.SaveChangesAsync();
            return candidate.Id;
        });
    }

    /// <summary>标记候选检测中并取出加密凭据；两分钟内的并发检测、不存在或已停用都拒绝（读「正在检测」）。</summary>
    private async Task<byte[]> BeginTest(long id)
    {
        await using var scope = scopes.CreateAsyncScope();
        var candidates = scope.Repository<UpstreamCandidate>();
        return await candidates.ExecuteInTransactionAsync(async () =>
        {
            var candidate = await candidates.GetByIdAsync(id) ?? throw new InvalidOperationException("这个上游正在检测，请稍后刷新页面查看结果。");
            candidate.BeginTest(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await candidates.SaveChangesAsync();
            return candidate.Credential;
        });
    }

    /// <summary>保存检测结果状态、报告与实际采用的配置；那一家这期间被停用了就什么都不写。</summary>
    private async Task FinishTest(long id, CandidateState state, string reportJson, string optionsJson)
    {
        await using var scope = scopes.CreateAsyncScope();
        var candidates = scope.Repository<UpstreamCandidate>();
        if (await candidates.GetByIdAsync(id) is not { } candidate) return;
        candidate.FinishTest(state, reportJson, optionsJson);
        await Commit(candidates, Changed);
    }

    /// <summary>写回获取模型或改选模型后的配置与报告（报告可为空），并把状态置为未测试；不存在时提示重新加载。</summary>
    private async Task ResetToUntested(long id, string optionsJson, string? reportJson)
    {
        await using var scope = scopes.CreateAsyncScope();
        var candidates = scope.Repository<UpstreamCandidate>();
        (await candidates.GetByIdAsync(id) ?? throw Missing()).ResetToUntested(optionsJson, reportJson);
        await Commit(candidates, Changed);
    }

    /// <summary>按主键读取已保存上游的加密凭据（导出用）；不存在时提示重新加载。</summary>
    private async Task<byte[]> ReadCredential(long id)
    {
        await using var scope = scopes.CreateAsyncScope();
        return (await scope.Repository<UpstreamCandidate>().GetByIdAsync(id) ?? throw Missing()).Credential;
    }

    /// <summary>在一个写事务里给这些已保存上游记下导出搬走的时刻；已记过的保留最早那次，有一个不存在就整批不记。</summary>
    private async Task MarkExported(IReadOnlyCollection<long> ids)
    {
        await using var scope = scopes.CreateAsyncScope();
        var candidates = scope.Repository<UpstreamCandidate>();
        await candidates.ExecuteInTransactionAsync(async () =>
        {
            var rows = await candidates.FindAsync(item => ids.Contains(item.Id));
            if (rows.Count != ids.Distinct().Count()) throw new InvalidOperationException("要导出的上游有一家已经不在了，请重新加载页面后再导出。");
            var now = DateTimeOffset.UtcNow;
            foreach (var row in rows) row.MarkExported(now);
            return await candidates.SaveChangesAsync();
        });
    }

    /// <summary>读活动上游行、它来自的那一家（停用的也读得到，只为取名称、对外编号与是否已导出）并解析配置；行缺失时抛出 <see cref="InvalidOperationException"/>。</summary>
    private async Task<(ActiveUpstream Row, UpstreamCandidate? Source, UpstreamOptions Options)> Active()
    {
        await using var scope = scopes.CreateAsyncScope();
        var row = await scope.Repository<ActiveUpstream>().GetByIdAsync(ActiveUpstream.SingletonId) ?? throw new InvalidOperationException("上游配置缺失。");
        var source = row.UpstreamCandidateId is { } id ? await scope.Repository<UpstreamCandidate>().GetByIdAsync(id, includeDeleted: true) : null;
        return (row, source, Parse<UpstreamOptions>(row.OptionsJson ?? "null"));
    }

    /// <summary>此刻活动上游来自的已保存上游主键；没有时为空。</summary>
    private async Task<long?> ActiveCandidateId()
    {
        await using var scope = scopes.CreateAsyncScope();
        return await ActiveCandidateId(scope);
    }

    /// <summary>在给定作用域里读活动上游来自的已保存上游主键。</summary>
    private static async Task<long?> ActiveCandidateId(AsyncServiceScope scope) =>
        (await scope.Repository<ActiveUpstream>().GetByIdAsync(ActiveUpstream.SingletonId))?.UpstreamCandidateId;

    /// <summary>此刻的活动版本；活动行缺失时为空。</summary>
    private async Task<long?> ActiveVersion()
    {
        await using var scope = scopes.CreateAsyncScope();
        return (await scope.Repository<ActiveUpstream>().GetByIdAsync(ActiveUpstream.SingletonId))?.Version;
    }

    /// <summary>提交一次不带条件的改写；同一行在读出与提交之间被另一页面或命令改过（行版本对不上）时读出 <paramref name="message"/>。</summary>
    private static async Task Commit<TEntity>(IRepository<TEntity> repository, string message) where TEntity : BaseEntity
    {
        try { await repository.SaveChangesAsync(); }
        // 并发令牌对不上说明有人刚改过这一行：不覆盖别人的改动，读出原因让管理员重新加载。
        catch (DbUpdateConcurrencyException) { throw new InvalidOperationException(message); }
    }

    /// <summary>已保存上游在读出与提交之间被改过的读句。</summary>
    private const string Changed = "这个上游刚被另一页面或命令改过，请重新加载页面再试。";

    /// <summary>「这个上游不存在」的读句。</summary>
    private static InvalidOperationException Missing() => new("这个上游不存在，请重新加载页面。");

    /// <summary>上游页的顺序：按创建先后，同一毫秒的按主键。</summary>
    private static IEnumerable<UpstreamCandidate> InPageOrder(IEnumerable<UpstreamCandidate> rows) => rows.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id);

    /// <summary>解析报告 JSON；没有时为 null。</summary>
    private static UpstreamReport? Report(string? json) => json is null ? null : Parse<UpstreamReport>(json);

    /// <summary>把库中的已保存上游行解析成可展示的视图，带上此刻能不能删。</summary>
    private static UpstreamCandidateView ToView(UpstreamCandidate row, long nowSeconds, long? activeCandidateId) => new(row.Id, row.Uid, Named(row.Name), row.ExpectedVersion,
        Parse<UpstreamOptions>(row.OptionsJson), row.State, Report(row.ReportJson), row.Deletable(nowSeconds, activeCandidateId),
        row.ExportedAt is { } at ? DateTimeOffset.FromUnixTimeMilliseconds(at) : null);

    /// <summary>名称只做显示：库里为空串（旧版迁移时地址解析不出主机名，没写占位文字）时读「未命名上游」，管理员改名即补上；为 null（活动上游不是从已保存上游来的）时原样。</summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(name))]
    private static string? Named(string? name) => name is "" ? "未命名上游" : name;

    /// <summary>按统一 JSON 选项序列化配置或报告。</summary>
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, ConfigurationFiles.Json);

    /// <summary>按统一 JSON 选项反序列化存储的配置或报告；内容为 JSON null 时抛出 <see cref="InvalidOperationException"/>。</summary>
    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, ConfigurationFiles.Json) ?? throw new InvalidOperationException("上游配置损坏。");

    /// <summary>
    /// 官方登录上游在标记检测开始之前先读它配置目录里的最新令牌，取不到时抛出 <see cref="InvalidOperationException"/> 提示重新登录，上游状态不变；
    /// 已导出搬走的不读令牌（读令牌可能触发续期，把新电脑上那份挤掉），直接拒绝。其余上游返回 null，由调用方解密库中凭据。
    /// </summary>
    private async Task<(string? Token, string? AccountUuid)> LoginToken(UpstreamCandidateView candidate, CancellationToken cancellation)
    {
        if (!candidate.Options.OfficialLogin) return (null, null);
        if (candidate.ExportedAt is not null) throw new InvalidOperationException($"「{candidate.Name}」已经导出搬走，本机不再用它，也不再获取模型或测试。不再需要它时可以删除。");
        var token = officialAccounts.ReadAccessToken(candidate.Uid) ?? throw new InvalidOperationException("这个官方账户的登录凭据已经不在了，请重新按「登录官方账户」。");
        // 每次获取模型和测试都照原生登录向官方取一次账户资料并存进账户目录：导入的账户（只有凭据、没有 oauthAccount）由此补上编号，换了账户由此同步；
        // 取不到就抛出，这次不获取、不测试，转发也因没有编号继续本地阻断。导入后必须测试通过才能切换，所以每个在用的官方账户都经过这一步。
        var profile = await probe.Profile(new(candidate.ExpectedVersion, candidate.Options, token), cancellation);
        officialAccounts.RecordAccount(candidate.Uid, profile);
        return (token, profile.AccountUuid);
    }

    /// <summary>解密凭据并返回 UTF-8 字符串，解密出的字节缓冲在返回前清零。</summary>
    private string Decrypt(byte[] encrypted)
    {
        var plain = protector.Unprotect(encrypted);
        try { return Encoding.UTF8.GetString(plain); }
        // 无论解码是否成功都清零解密出的字节，缩短明文凭据在内存中的停留时间。
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
