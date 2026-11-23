using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Security.Cryptography;
using Application.Contracts.Uploads;
using Application.Contracts.Upstream;
using Application.Interfaces.ClaudeCode;
using Application.Interfaces.Identity;
using Application.Interfaces.Upstream;
using Core.Upstream;

namespace HouseholdRelay.Pages;

/// <summary>上游接入页：起名字、填地址、贴密钥、获取模型、按写明费用的按钮测试，再按名称切换；认证方式与检测方式由程序探测。</summary>
public sealed class UpstreamModel(IUpstreamService upstreams, IUpstreamChecks checks, IUpstreamPool upstreamPool, IClaudeCodeIdentitySource identities, IClaudeCodeUpdater updater, IIdentityService people) : PageModel
{
    /// <summary>要在页面上读出的那次后台检测：刚建的、地址里点名的，或正在设置的那一家正在进行的；没有时为空。进行中时页面放状态区和「取消检测」，结束了读出结论。</summary>
    public UpstreamCheck? Check { get; private set; }

    /// <summary>「API 供应商」表单。</summary>
    [BindProperty] public UpstreamInput Api { get; set; } = new();
    /// <summary>「官方账户」里贴账户访问令牌的表单；地址默认官方接口。</summary>
    [BindProperty] public UpstreamInput Account { get; set; } = new() { BaseUrl = "https://" + UpstreamDetection.OfficialHost };
    /// <summary>「官方账户」里「登录官方账户」的表单：名称可空（用登录邮箱），地址默认官方接口。</summary>
    [BindProperty] public UpstreamInput Login { get; set; } = new() { BaseUrl = "https://" + UpstreamDetection.OfficialHost };
    /// <summary>「官方账户」里「导入登录文件」的表单：名称可空（用登录邮箱），地址默认官方接口。</summary>
    [BindProperty] public UpstreamInput Import { get; set; } = new() { BaseUrl = "https://" + UpstreamDetection.OfficialHost };
    /// <summary>要导入的 <c>.credentials.json</c>；处理完无论成败都清空，不回显到页面。</summary>
    [BindProperty] public IFormFile? ImportFile { get; set; }
    /// <summary>要导入的上游导出文件；处理完无论成败都清空，不回显到页面。</summary>
    [BindProperty] public IFormFile? UpstreamFile { get; set; }
    /// <summary>打开页面时的活动上游版本号；保存与切换时若活动版本已变化则拒绝，防止基于过期页面操作。</summary>
    [BindProperty] public long Version { get; set; }
    /// <summary>当前活动上游的非秘密部分；读它不等官方登录令牌刷新。</summary>
    public UpstreamOverview Active { get; private set; } = null!;
    /// <summary>全部已保存的上游，按创建先后排列。</summary>
    public IReadOnlyList<UpstreamCandidateView> Saved { get; private set; } = [];
    /// <summary>正在设置的上游（刚获取模型、或在列表里点了「选择模型并测试」）；没有时不显示第二步。</summary>
    public UpstreamCandidateView? Working { get; private set; }
    /// <summary>操作成功后的提示，例如获取模型的结果、测试读句或已切换。</summary>
    public string? Notice { get; private set; }
    /// <summary>活动上游的运行状态文字：未配置、凭据失效、冷却中（含剩余秒数）、已通过检测或尚未检测。</summary>
    public string RuntimeStatus { get; private set; } = "";
    /// <summary>此刻实际走的是哪家、哪家暂避到几点、为什么，按读屏顺序读成一句话。</summary>
    public string RoutingText { get; private set; } = "";
    /// <summary>发往官方账户的请求用的是哪份身份、此刻取没取到，读成一句话；取不到时是与客户端、用量页同一句原因与下一步。</summary>
    public string IdentityText { get; private set; } = "";
    /// <summary>中转与客户端的 Claude Code 版本、谁新、上次升级的时间与结果，读成一段话。</summary>
    public string UpdateText { get; private set; } = "";
    /// <summary>官方账户小节里关于系统提示环境段的提醒：中转不改写它，官方看得到客户端的系统和目录。</summary>
    public const string EnvironmentNote = "官方能从系统提示里看到客户端的系统、命令行和工作目录（中转不改写它们，改了模型会按错的目录和系统给命令）。官方账户宜在与中转同一种系统的电脑上用。";
    /// <summary>在用的那一家已导出搬走时读出的说明与下一步；没有搬走时为 null。</summary>
    public string? MovedText { get; private set; }
    /// <summary>在用的是登录得到、还没搬走的官方账户时，导出表单里事先读出「勾选它导出后会怎样」；否则为 null。</summary>
    public string? ExportWarning { get; private set; }
    /// <summary>刚导出成功时的下载地址（加密文件的 data: 地址，页面打开即自动下载一次）；其余时候为 null。</summary>
    public string? ExportDownload { get; private set; }
    /// <summary>刚导出的文件名。</summary>
    public string ExportFileName { get; private set; } = "";
    /// <summary>从菜单「改名」进来、要在那一家下面展开改名小表单的上游主键；没有时为 null。</summary>
    public long? RenameId { get; private set; }
    /// <summary>从菜单「删除」进来、要在那一家下面展开删除确认小表单的上游主键；没有时为 null。</summary>
    public long? DeleteId { get; private set; }
    /// <summary>从菜单「导出」进来时只预选的那一家；没有时导出表单默认全选。</summary>
    public long? ExportId { get; private set; }
    /// <summary>回发后焦点要落的元素编号（刚处理的那一家的按钮、刚打开的小表单的第一项、删除后的下一家）；为空时由站点脚本按结果提示或错误决定。</summary>
    public string? FocusId { get; private set; }

    /// <summary>改名小表单里新名称输入框的字段键。</summary>
    public const string NewNameKey = "newName";
    /// <summary>删除确认勾选框的字段键。</summary>
    public const string DeleteConfirmedKey = "deleteConfirmed";
    /// <summary>测试小表单里模型输入的字段键。</summary>
    public const string ModelKey = "model";
    /// <summary>导出时「再输入一次导出密码」的字段键。</summary>
    public const string ExportAgainKey = "exportPasswordAgain";

    /// <summary>打开上游页（需已登录）：<paramref name="candidateId"/> 指定时把它作为正在设置的上游，否则取最近一个还没测通过的；<paramref name="checkId"/> 指定时读出那次后台检测的状态或结论；只读。</summary>
    /// <remarks>
    /// 菜单进来的小表单：<paramref name="rename"/> 展开那一家的改名，<paramref name="delete"/> 展开删除确认，<paramref name="export"/> 让导出只预选那一家；
    /// 点名 <paramref name="candidateId"/> 时展开选择模型并测试。焦点进刚展开的那一个小表单的第一项。
    /// </remarks>
    public async Task OnGetAsync(long? candidateId, long? checkId, long? rename, long? delete, long? export)
    {
        await LoadAsync(candidateId, checkId);
        RenameId = Saved.Any(item => item.Id == rename) ? rename : null;
        DeleteId = Saved.Any(item => item.Id == delete && item.Deletable) ? delete : null;
        ExportId = Saved.Any(item => item.Id == export && !item.Damaged) ? export : null;
        FocusId = RenameId is not null ? "rename-name" : DeleteId is not null ? "delete-confirm" : ExportId is not null ? "export-password"
            : candidateId is not null && Working?.Id == candidateId ? "test-model" : null;
    }

    /// <summary>查一次后台检测的状态（需已登录）：页面脚本每秒查一次，结束时把结论放进状态区读一次。编号不认识时回 404。</summary>
    public IActionResult OnGetCheck(long checkId) => checks.Read(checkId) is { } check
        ? new JsonResult(new { ended = check.Ended, status = check.Status.ToString(), message = check.Message, candidateId = check.UpstreamCandidateId })
        : NotFound();

    /// <summary>取消检测（需已登录且通过防 CSRF 校验）：只发出取消，那一家由检测收尾写成「检测未完成」；页面读出取消结果。</summary>
    public async Task<IActionResult> OnPostCancelCheckAsync(long checkId)
    {
        ModelState.Clear();
        var check = checks.Read(checkId);
        if (!checks.Cancel(checkId)) ModelState.AddModelError("", "这次检测已经结束了，不用取消。结果在下面。");
        // 取消后等它收尾一小会儿（最多 5 秒），页面直接读出「检测未完成」，不用再按刷新；等不到就读出「正在取消」，状态区随后自动读出结论。
        else for (var waited = 0; waited < 50 && checks.Read(checkId) is { Ended: false }; waited++) await Task.Delay(100, HttpContext.RequestAborted);
        await LoadAsync(check?.UpstreamCandidateId, checkId); return Page();
    }

    /// <summary>API 供应商：加密保存名称、地址、密钥后立刻获取模型（不花额度）；密钥只在这一次提交里出现，处理后清空。</summary>
    public Task<IActionResult> OnPostDiscoverApiAsync() => DiscoverAsync(Api, Api.ToApiOptions());

    /// <summary>官方账户：加密保存名称、地址、账户令牌后立刻获取模型（不花额度）。</summary>
    public Task<IActionResult> OnPostDiscoverAccountAsync() => DiscoverAsync(Account, Account.ToAccountOptions());

    /// <summary>
    /// 登录官方账户（需已登录且通过防 CSRF 校验）：委托本机 Claude Code 打开浏览器登录，等登录完成后保存为具名上游并立刻获取模型（不花额度）；
    /// 浏览器断开时经 RequestAborted 结束登录进程。未安装、取消、超时等原因作为表单错误读出。
    /// </summary>
    public async Task<IActionResult> OnPostLoginAccountAsync()
    {
        ModelState.Clear();
        long? working = null; long? check = null;
        try
        {
            var id = await upstreams.LoginOfficialAsync(Version, Login.ToLoginOptions(), Login.Name, HttpContext.RequestAborted); working = id;
            check = checks.StartDiscovery(id, $"官方账户登录成功，已保存为「{(await upstreams.ReadCandidateAsync(id)).Name}」。");
            Login = new() { BaseUrl = "https://" + UpstreamDetection.OfficialHost }; FocusId = ButtonId(id);
        }
        // 未安装、登录取消或超时、代理取不到、名称不合规则或活动版本已变化：只作为表单错误读出，不改动活动上游。
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        await LoadAsync(working, check); return Page();
    }

    /// <summary>
    /// 重新登录（需已登录且通过防 CSRF 校验）：对菜单里那一家登录得到的官方账户委托本机 Claude Code 再登录一次，是原来那个账户就换上新凭据（主键、思域设置不变），
    /// 随后读一次模型目录（不花额度），焦点回这一家的按钮；登成了别的账户、取消、超时等原因作为页面错误读出，原凭据不动。浏览器断开时经 RequestAborted 结束登录进程。
    /// </summary>
    public async Task<IActionResult> OnPostReloginAsync(long candidateId)
    {
        ModelState.Clear();
        long? check = null;
        try
        {
            var name = DisplayName(await upstreams.ReadCandidateAsync(candidateId));
            await upstreams.ReloginAsync(candidateId, HttpContext.RequestAborted);
            check = checks.StartDiscovery(candidateId, $"已重新登录「{name}」，还是原来那个官方账户，换上了新的登录凭据；这家上游的主键和思域的设置都不用改。");
            FocusId = ButtonId(candidateId);
        }
        // 不是登录得到的官方账户、已搬走、登成了别的账户、取消或超时：读出原因与下一步，原凭据不动。
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        await LoadAsync(candidateId, check); return Page();
    }

    /// <summary>
    /// 导入登录文件（需已登录且通过防 CSRF 校验）：限长读入上传的 <c>.credentials.json</c>，交给服务校验并放进新上游自己的账户目录，保存后立刻获取模型（不花额度）；
    /// 文件字节用完即清零，文件与名称以外的绑定值不回显。文件不合格、未安装 Claude Code 等原因作为表单错误读出。
    /// </summary>
    public async Task<IActionResult> OnPostImportAccountAsync()
    {
        ModelState.Clear();
        long? working = null; long? check = null; byte[]? content = null;
        try
        {
            if (ImportFile is null || ImportFile.Length == 0) throw new InvalidOperationException("请先选择要导入的登录文件（.credentials.json），再按「导入登录文件」。");
            // 最多读到上限多一个字节：够判断超限，又不把超大的上传整份读进内存；是否超限由服务那一处判定、读出。
            content = new byte[(int)Math.Min(ImportFile.Length, UploadLimits.OfficialCredentialBytes + 1)];
            await using (var input = ImportFile.OpenReadStream())
                if (await input.ReadAtLeastAsync(content, content.Length, false, HttpContext.RequestAborted) < content.Length)
                    throw new InvalidOperationException("登录文件没有读完整，请重新选择后再按「导入登录文件」。");
            var id = await upstreams.ImportOfficialAsync(Version, Import.ToLoginOptions(), content, Import.Name, HttpContext.RequestAborted); working = id;
            check = checks.StartDiscovery(id, $"登录文件已导入，已保存为「{(await upstreams.ReadCandidateAsync(id)).Name}」。");
            Import = new() { BaseUrl = "https://" + UpstreamDetection.OfficialHost }; FocusId = ButtonId(id);
        }
        // 文件不合格、未安装 Claude Code、代理取不到、名称不合规则或活动版本已变化：只作为表单错误读出，不改动活动上游。
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        // 登录文件里有访问令牌和刷新令牌：无论成败都清零字节、清掉文件绑定值。
        finally { if (content is not null) CryptographicOperations.ZeroMemory(content); ImportFile = null; }
        await LoadAsync(working, check); return Page();
    }

    /// <summary>
    /// 测试（需已登录且通过防 CSRF 校验）：按钮文字已写明会消耗少量上游额度，按下即视为同意；先按需改选模型，再在后台对已保存的上游做一次最小调用。
    /// 页面马上回来，状态区自动读出结论，可按「取消检测」；测试不会切换当前上游。
    /// </summary>
    public async Task<IActionResult> OnPostTestAsync(long candidateId, string? model)
    {
        ModelState.Clear();
        long? check = null;
        try
        {
            if (model is not null) await upstreams.ChooseModelAsync(candidateId, model);
            check = checks.StartTest(candidateId); FocusId = ButtonId(candidateId);
        }
        // 未选模型、模型名不合规则、上游不存在或正在检测：挂到测试小表单的模型输入上读出，不改动活动上游。
        catch (InvalidOperationException error) { ModelState.AddModelError(ModelKey, error.Message); }
        await LoadAsync(candidateId, check); return Page();
    }

    /// <summary>切换（需已登录且通过防 CSRF 校验）：把测试通过的上游原子切换为活动版本，核对的是本页打开时看到的版本；在途请求继续用旧版本，个人令牌和中转地址不变。</summary>
    public async Task<IActionResult> OnPostActivateAsync(long candidateId)
    {
        ModelState.Clear();
        try
        {
            await upstreams.ActivateAsync(candidateId, Version);
            Notice = $"已切换到「{(await upstreams.ReadCandidateAsync(candidateId)).Name}」。后续请求使用它；个人令牌和中转地址不用换。"; FocusId = ButtonId(candidateId);
        }
        // 没测通过或活动版本已被别处改动：读出原因，活动上游保持原样。
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        await LoadAsync(null); return Page();
    }

    /// <summary>升级中转的 Claude Code（需已登录且通过防 CSRF 校验）：用本机安装方式自带的升级，后台已在升级时等那一次；页面读出结果，失败时带升级命令原话。</summary>
    public async Task<IActionResult> OnPostUpgradeClaudeCodeAsync(CancellationToken cancellation)
    {
        ModelState.Clear();
        Notice = await updater.UpdateAsync(cancellation);
        await LoadAsync(null); return Page();
    }

    /// <summary>保存自动切换开关（需已登录且通过防 CSRF 校验）：保存即生效，下一个请求就按新开关走，不用重启，也不改在用的那一家。</summary>
    public async Task<IActionResult> OnPostFailoverAsync(bool failover)
    {
        ModelState.Clear();
        await upstreams.SetFailoverAsync(failover);
        FocusId = "failover-switch";
        Notice = failover ? "自动切换已打开：在用的上游失败时，会按顺序换到其他测试通过的上游。" : "自动切换已关闭：只用在用的那一家，它失败时把错误直接交给客户端。";
        await LoadAsync(null); return Page();
    }

    /// <summary>
    /// 导出（需已登录且通过防 CSRF 校验）：两次输入的导出密码一致后，把勾选的上游加密成一个文件，页面读出结果并自动下载；密码不保存、不回显。
    /// 勾选的官方账户随之搬走，此后本机不再用它转发、不再续期。密码不合规则、没勾或上游已不在时作为表单错误读出，什么都不改。
    /// </summary>
    public async Task<IActionResult> OnPostExportAsync(long[]? exportIds, string? exportPassword, string? exportPasswordAgain)
    {
        ModelState.Clear();
        try
        {
            if (exportPassword != exportPasswordAgain)
            {
                ModelState.AddModelError(ExportAgainKey, "两次输入的导出密码不一样，请重新输入两遍，再按「导出加密文件」。");
                await LoadAsync(null); return Page();
            }
            var export = await upstreams.ExportAsync(exportIds ?? [], exportPassword ?? "");
            ExportFileName = $"household-relay-upstreams-{DateTimeOffset.Now:yyyyMMdd-HHmm}.hrupstreams";
            ExportDownload = "data:application/octet-stream;base64," + Convert.ToBase64String(export.File);
            Notice = $"已导出 {export.Names.Count} 家上游：{Quoted(export.Names)}。浏览器正在下载「{ExportFileName}」；没有自动下载时，按下面的「下载导出文件」。"
                + (export.Moved.Count == 0 ? "" : $"官方账户{Quoted(export.Moved)}已经搬走：本机不再用它转发、不再续期。")
                + "下一步：把文件拷到新电脑，在它的上游页「导入上游」里选这个文件，输入同一个导出密码。";
        }
        // 两次密码不一样、密码长度不对、没勾、上游已不在、官方账户凭据不在或续期没结束：读出原因，什么都不改。
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        await LoadAsync(null); return Page();
    }

    /// <summary>
    /// 导入上游（需已登录且通过防 CSRF 校验）：限长读入导出文件，用导入密码解开后按文件顺序加成未测试上游，页面逐家读出结果与下一步；
    /// 文件字节用完即清零，文件与密码都不回显。文件或密码不对时一家都不写，作为表单错误读出。
    /// </summary>
    public async Task<IActionResult> OnPostImportUpstreamsAsync(string? importPassword)
    {
        ModelState.Clear();
        byte[]? content = null;
        try
        {
            if (UpstreamFile is null || UpstreamFile.Length == 0) throw new InvalidOperationException("请先选择旧电脑上导出的文件，再按「导入上游」。");
            // 最多读到上限多一个字节：够判断超限，又不把超大的上传整份读进内存；是否超限由服务那一处判定、读出。
            content = new byte[(int)Math.Min(UpstreamFile.Length, UploadLimits.UpstreamExportBytes + 1)];
            await using (var input = UpstreamFile.OpenReadStream())
                if (await input.ReadAtLeastAsync(content, content.Length, false, HttpContext.RequestAborted) < content.Length)
                    throw new InvalidOperationException("导出文件没有读完整，请重新选择后再按「导入上游」。");
            var outcomes = await upstreams.ImportAsync(Version, content, importPassword ?? "", HttpContext.RequestAborted);
            var added = outcomes.Where(item => item.Error is null).Select(item => item.Name).ToArray();
            var failed = outcomes.Where(item => item.Error is not null).Select(item => $"「{item.Name}」：{item.Error}").ToArray();
            var next = "下一步：逐家按「选择模型并测试」，测试通过后再切换。";
            if (failed.Length == 0) Notice = $"已导入 {added.Length} 家上游：{Quoted(added)}，都排在已有上游后面，尚未测试。{next}";
            else if (added.Length == 0) ModelState.AddModelError("", $"文件里的 {failed.Length} 家上游一家都没导入。{string.Join("", failed)}");
            else Notice = $"导入了 {added.Length} 家上游：{Quoted(added)}，尚未测试；{failed.Length} 家没导入。{string.Join("", failed)}{next}没导入的按原因处理后，在本机重新添加。";
        }
        // 文件不对、格式版本不认识、密码不对、文件被改过或活动版本已变化：读出原因，一家都不写。
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        // 导出文件里有全部密钥和登录文件（虽已加密）：无论成败都清零字节、清掉文件绑定值。
        finally { if (content is not null) CryptographicOperations.ZeroMemory(content); UpstreamFile = null; }
        await LoadAsync(null); return Page();
    }

    /// <summary>改名（需已登录且通过防 CSRF 校验）：只改显示名称，切换和用量都按主键关联，不受影响。</summary>
    public async Task<IActionResult> OnPostRenameAsync(long candidateId, string? newName)
    {
        ModelState.Clear();
        try { await upstreams.RenameAsync(candidateId, newName ?? ""); Notice = $"已改名为「{(await upstreams.ReadCandidateAsync(candidateId)).Name}」。"; FocusId = ButtonId(candidateId); }
        // 名称为空、过长或上游不存在：挂到改名小表单的输入框上读出，小表单留着，名称保持原样。
        catch (InvalidOperationException error) { ModelState.AddModelError(NewNameKey, error.Message); RenameId = candidateId; }
        await LoadAsync(null);
        if (RenameId is not null && Saved.All(item => item.Id != RenameId)) RenameId = null;
        return Page();
    }

    /// <summary>
    /// 重新获取模型（需已登录且通过防 CSRF 校验）：对已保存的上游用库里加密的密钥（官方登录的用账户目录里的令牌）再读一次模型目录，不花额度、不新增上游；
    /// 完成后把它作为正在设置的上游。浏览器断开时经 RequestAborted 取消。
    /// </summary>
    public async Task<IActionResult> OnPostRediscoverAsync(long candidateId)
    {
        ModelState.Clear();
        long? check = null;
        try { check = checks.StartDiscovery(candidateId, $"已重新获取「{(await upstreams.ReadCandidateAsync(candidateId)).Name}」的模型。"); FocusId = ButtonId(candidateId); }
        // 上游不存在或正在检测：读出原因；上游自己的拒绝由目录读句说明，不走这里。
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        await LoadAsync(candidateId, check); return Page();
    }

    /// <summary>删除（需已登录且通过防 CSRF 校验）：须勾选确认（没勾时错误挂到勾选框上，确认小表单留着）；当前使用中或正在检测的上游由服务拒绝。删除成功后焦点去下一家。</summary>
    public async Task<IActionResult> OnPostDeleteAsync(long candidateId, bool deleteConfirmed)
    {
        ModelState.Clear();
        // 删除后焦点去列表里原来排在它后面的那一家；它是最后一家就去前一家；一家都不剩就去列表标题。
        var before = (await upstreams.ListCandidatesAsync()).Select(item => item.Id).ToList(); var position = before.IndexOf(candidateId);
        try
        {
            var name = (await upstreams.ReadCandidateAsync(candidateId)).Name;
            if (!deleteConfirmed)
            {
                ModelState.AddModelError(DeleteConfirmedKey, $"要删除「{name}」，请先勾选删除按钮前面的确认框，再按删除。");
                await LoadAsync(null); DeleteId = Saved.Any(item => item.Id == candidateId && item.Deletable) ? candidateId : null; return Page();
            }
            var references = await upstreams.DeleteAsync(candidateId);
            Notice = references is { Requests: 0, Attempts: 0, Prices: 0 } ? $"已删除「{name}」，没有用量或单价引用它，本机已清除它的密钥。"
                : $"已删除「{name}」，本机已清除它的密钥。它有 {references.Requests} 条用量记录、{references.Attempts} 次上游尝试、{references.Prices} 条单价，"
                    + "历史照常保留，用量页仍读出它的名字。";
            var left = before.Where(id => id != candidateId).ToList();
            FocusId = left.Count == 0 ? "saved-heading" : ButtonId(left[Math.Min(Math.Max(position, 0), left.Count - 1)]);
        }
        // 当前使用中、正在检测或上游不存在：读出原因，什么都不删。
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        await LoadAsync(null); return Page();
    }

    /// <summary>两张表单共用的保存加获取模型：成功后清空表单并把它作为正在设置的上游；任一步拒绝都读出原因。密钥无论成败都清空。</summary>
    private async Task<IActionResult> DiscoverAsync(UpstreamInput input, UpstreamOptions options)
    {
        long? working = null; long? check = null;
        try
        {
            var id = await upstreams.SaveCandidateAsync(Version, options, input.Credential ?? "", input.Name ?? ""); working = id;
            check = checks.StartDiscovery(id, $"已保存「{(await upstreams.ReadCandidateAsync(id)).Name}」。"); FocusId = ButtonId(id);
            ModelState.Clear(); Api = new(); Account = new() { BaseUrl = "https://" + UpstreamDetection.OfficialHost };
        }
        // 名称、地址、密钥不合规则或活动版本已变化：只作为表单错误读出；已保存但获取模型被拒的情况由目录读句说明，不走这里。
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        var prefix = ReferenceEquals(input, Api) ? nameof(Api) : nameof(Account);
        // 误把密钥贴进名称或地址时，这两栏也不能原样回填，否则密钥会出现在页面上。
        if (input.Credential is { Length: >= 8 } secret)
        {
            if (input.Name?.Contains(secret, StringComparison.Ordinal) == true) { input.Name = null; ModelState.Remove(prefix + "." + nameof(UpstreamInput.Name)); }
            if (input.BaseUrl?.Contains(secret, StringComparison.Ordinal) == true) { input.BaseUrl = null; ModelState.Remove(prefix + "." + nameof(UpstreamInput.BaseUrl)); }
        }
        input.Credential = null; ModelState.Remove(prefix + "." + nameof(UpstreamInput.Credential));
        await LoadAsync(working, check); return Page();
    }

    /// <summary>重新读取页面，不点名后台检测（正在设置的那一家有进行中的检测时照样读出）。</summary>
    private Task LoadAsync(long? workingId) => LoadAsync(workingId, null);

    /// <summary>
    /// 重新读取活动上游、已保存列表、正在设置的上游和上游池运行状态，并把版本号更新为最新；再读出要读的那次后台检测：
    /// 点名的（<paramref name="checkId"/>），没点名就取正在设置的那一家进行中的。已结束的把结论放进提示（没走到结论的放进错误）。
    /// </summary>
    private async Task LoadAsync(long? workingId, long? checkId)
    {
        Active = await upstreams.ReadOverviewAsync(); Saved = await upstreams.ListCandidatesAsync(); Version = Active.Version; ModelState.Remove(nameof(Version));
        // 其他按钮的提交不带官方账户表单字段，绑定会给出空地址；补回官方地址，避免读屏读到一个空的必填框。
        if (string.IsNullOrWhiteSpace(Account.BaseUrl)) Account.BaseUrl = "https://" + UpstreamDetection.OfficialHost;
        if (string.IsNullOrWhiteSpace(Login.BaseUrl)) Login.BaseUrl = "https://" + UpstreamDetection.OfficialHost;
        if (string.IsNullOrWhiteSpace(Import.BaseUrl)) Import.BaseUrl = "https://" + UpstreamDetection.OfficialHost;
        // 已导出搬走的不再获取模型、测试，不作为正在设置的上游。
        // 配置损坏的那一家也不作为正在设置的上游：它只能删除。
        Working = (workingId is null ? Saved.LastOrDefault(item => item.State != CandidateState.Passed && !item.Damaged) : Saved.FirstOrDefault(item => item.Id == workingId)) is { ExportedAt: null, Damaged: false } chosen ? chosen : null;
        Check = checkId is { } named ? checks.Read(named) : Working is { } current ? checks.Running(current.Id) : null;
        if (Check is { Ended: true } ended)
        {
            if (ended.Status == UpstreamCheckStatus.Failed) ModelState.AddModelError("", ended.Message);
            else Notice ??= ended.Message;
        }
        var inUse = Saved.FirstOrDefault(item => item.Id == Active.UpstreamCandidateId);
        MovedText = inUse?.ExportedAt is null ? null : MovedSentence(inUse.Name, Active.Failover);
        ExportWarning = inUse is { ExportedAt: null, Options.OfficialLogin: true }
            ? $"在用的「{inUse.Name}」是登录得到的官方账户，勾选它导出后它就搬走了：" + (Active.Failover
                ? "自动切换开着，之后的请求会按「上游列表」的顺序换到其他测试通过的上游。"
                : "自动切换关着，导出后中转会没有可用上游。请先切换到别的上游，或者打开自动切换，再导出。")
            : null;
        var state = upstreamPool.Status(Active.Version);
        RuntimeStatus = Active.RuntimeText(MovedText is not null, state.Paused, state.CooldownSeconds);
        RoutingText = RoutingSentence(upstreamPool, Active, Saved);
        IdentityText = identities.Describe();
        var users = await people.ListAsync();
        UpdateText = updater.Describe(id => users.FirstOrDefault(user => user.Id == id)?.DisplayName ?? "已删除的使用者");
    }

    /// <summary>上游页与概览共用的走法读句：名称按主键从已保存上游里查，查不到时读「已删除的上游」，在用的不是从已保存上游切换来的读「未命名上游」。</summary>
    public static string RoutingSentence(IUpstreamPool pool, UpstreamOverview active, IReadOnlyList<UpstreamCandidateView> saved) =>
        pool.Routing().Describe(active.Failover, active.UpstreamCandidateId, id => saved.FirstOrDefault(item => item.Id == id)?.Name ?? (id is null ? "未命名上游" : "已删除的上游"));

    /// <summary>在用的那一家已导出搬走时的读句：本机不再用它，按自动切换开关说明请求此刻怎么走，再给下一步；上游页与概览共用。</summary>
    public static string MovedSentence(string name, bool failover) => $"在用的「{name}」已经导出搬走，本机不再用它转发、不再续期。"
        + (failover ? "自动切换开着：请求按「上游列表」的顺序交给其他测试通过的上游。" : "自动切换关着：中转现在没有可用上游，家里的请求会失败。")
        + $"下一步：在「上游列表」里切换到别的上游；不再需要「{name}」时可以删除它。";

    /// <summary>上游列表里那一家的菜单按钮的元素编号；焦点回到这一家时用它。</summary>
    public static string ButtonId(long id) => "upstream-" + id;

    /// <summary>名称的读法：旧版迁移来的空名读「未命名上游」。</summary>
    public static string DisplayName(UpstreamCandidateView item) => item.Name.Length == 0 ? "未命名上游" : item.Name;

    /// <summary>
    /// 列表里每一家的按钮名：「上游名，状态」，在用的那一家加「在用」，例如「甲中转，在用，已通过」。
    /// 状态只读服务给的检测阶段与结论：配置已损坏、已导出搬走、正在检测、检测未完成、已通过、未通过、尚未测试。
    /// </summary>
    public static string ButtonName(UpstreamCandidateView item, long? activeId) => DisplayName(item) + "，" + (item.Damaged ? "配置已损坏"
        : item.ExportedAt is not null ? "已导出搬走"
        : (item.Id == activeId ? "在用，" : "") + item.Phase switch
        {
            UpstreamCheckPhase.Checking => "正在检测",
            UpstreamCheckPhase.Interrupted => "检测未完成",
            _ => item.State switch { CandidateState.Passed => "已通过", CandidateState.Failed => "未通过", _ => "尚未测试" },
        });

    /// <summary>菜单项的文字；能不能出现由 <see cref="UpstreamCandidateView.Actions"/> 定，这里只管读法。</summary>
    public static string ActionLabel(UpstreamAction action) => action switch
    {
        UpstreamAction.Activate => "切换到这一家", UpstreamAction.ViewUsage => "查看用量", UpstreamAction.Test => "选择模型并测试（会消耗少量上游额度）",
        UpstreamAction.Relogin => "重新登录（主键和思域设置不变）", UpstreamAction.Rediscover => "重新获取模型（不花额度）", UpstreamAction.Rename => "改名", UpstreamAction.Export => "导出", UpstreamAction.Delete => "删除",
    };

    /// <summary>把名称列成「甲」「乙」的读法。</summary>
    private static string Quoted(IEnumerable<string> names) => string.Concat(names.Select(name => $"「{name}」"));

    /// <summary>「接入方式」的读法：官方账户登录、官方账户令牌或 API 密钥。</summary>
    public static string AccessText(UpstreamOptions options) => options.OfficialLogin ? "官方账户登录" : options.Mode == UpstreamMode.OAuth ? "官方账户令牌" : "API 密钥";

    /// <summary>列表与第二步里「认证方式」的读法：手动指定或已被上游确认时读出方式，否则读「尚未确认」。</summary>
    public static string AuthenticationText(UpstreamCandidateView item) =>
        item.Options.AuthenticationManual || item.Options.Mode == UpstreamMode.OAuth || item.Report?.Authentication == CheckStatus.Passed
            ? UpstreamDetection.AuthenticationLabel(item.Options.Authentication) + (item.Options.AuthenticationManual ? "（手动指定）" : "")
            : "尚未确认";

    /// <summary>
    /// 列表里「最近一次检测」的读法：配置已损坏、已导出搬走、正在检测、检测未完成（停在检测中却已过窗口），其余按结论读通过、未通过（附原因）或尚未测试。
    /// 「正在检测」「检测未完成」只读服务给的检测阶段（<see cref="UpstreamCandidateView.Phase"/>），不看原始状态。
    /// </summary>
    public static string ResultText(UpstreamCandidateView item) => item.Damaged
        ? "配置已损坏：读不懂这一家存的配置，不能获取模型、测试、切换或导出。请删除它，再重新添加。"
        : item.ExportedAt is { } moved ? $"已导出搬走（{moved.ToLocalTime():yyyy-MM-dd HH:mm}）：本机不再用它转发、不再续期。不再需要时可以删除。"
        : item.Phase switch
        {
            UpstreamCheckPhase.Checking => "正在检测，结果出来会自动读出。",
            UpstreamCheckPhase.Interrupted => UpstreamReport.UnfinishedLead + UpstreamReport.WindowReason + UpstreamReport.UnfinishedNext,
            _ => item.State == CandidateState.Passed ? $"测试通过（{item.Report?.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm}）。{item.Report?.Message}"
                : item.State == CandidateState.Failed ? "测试未通过：" + item.Report?.Message
                : item.Report?.Message ?? "尚未测试",
        };
}
