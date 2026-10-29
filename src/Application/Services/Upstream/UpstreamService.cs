using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Interfaces.Security;
using Application.Interfaces.Upstream;
using Core.Configuration;
using Core.Upstream;

namespace Application.Services.Upstream;

/// <summary>网页和 CLI 共用的候选保存、诊断和原子激活服务。</summary>
public sealed class UpstreamService(IUpstreamRepository repository, ISecretProtector protector, IUpstreamProbe probe) : IUpstreamService
{
    /// <summary>读取活动上游并解密其凭据组成快照；活动行缺失或配置 JSON 损坏时抛出 <see cref="InvalidOperationException"/>，未配置凭据时快照的凭据为 null。</summary>
    public UpstreamSnapshot ReadActive()
    {
        var row = repository.ReadActive() ?? throw new InvalidOperationException("上游配置缺失。");
        return new(row.Version, Parse<UpstreamOptions>(row.OptionsJson), row.ProtectedCredential is null ? null : Decrypt(row.ProtectedCredential),
            row.ReportJson is null ? null : Parse<UpstreamReport>(row.ReportJson));
    }

    /// <summary>读取最近创建的候选（不含凭据）；没有任何候选时返回 null。</summary>
    public UpstreamCandidate? LatestCandidate()
    {
        var id = repository.LatestCandidateId();
        return id is null ? null : ReadCandidate(id);
    }

    /// <summary>按主键读取候选的非秘密部分（基于的活动版本、配置、状态与检测报告）；不存在时抛出 <see cref="InvalidOperationException"/> 提示重新加载。</summary>
    public UpstreamCandidate ReadCandidate(string id)
    {
        var row = repository.ReadCandidate(id) ?? throw new InvalidOperationException("候选配置不存在，请重新加载页面。");
        return new(id, row.ExpectedVersion, Parse<UpstreamOptions>(row.OptionsJson), row.State,
            row.ReportJson is null ? null : Parse<UpstreamReport>(row.ReportJson));
    }

    /// <summary>
    /// 校验配置与凭据后加密保存为未测试候选，返回候选主键：凭据须为 1–8192 个字符且不含空白和控制字符；
    /// 凭据长度不少于 8 时，若出现在配置 JSON 或（转义后）出现在地址中则拒绝，防止秘密被写进会明文展示的字段。
    /// 活动版本已不是 <paramref name="expectedVersion"/> 时由仓储拒绝并提示重新加载。
    /// </summary>
    public string SaveCandidate(long expectedVersion, UpstreamOptions options, string credential)
    {
        options = options.Validated();
        if (credential.Length is < 1 or > 8192 || credential.Any(char.IsControl) || credential.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("请填写有效的上游凭据，不能含空白或控制字符。");
        var json = JsonSerializer.Serialize(options, ConfigurationFiles.Json);
        if (credential.Length >= 8 && (json.Contains(credential, StringComparison.Ordinal) || options.BaseUrl.Contains(Uri.EscapeDataString(credential), StringComparison.Ordinal)))
            throw new InvalidOperationException("凭据只能填写在专用秘密字段，不能写入地址、代理或模型名称。");
        var plain = Encoding.UTF8.GetBytes(credential);
        try
        {
            var encrypted = protector.Protect(plain); var id = Guid.NewGuid().ToString("N");
            repository.InsertCandidate(id, expectedVersion, json, encrypted);
            return id;
        }
        // 无论加密或写库是否成功都清零凭据明文字节，避免秘密残留在内存中。
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>
    /// 对候选做一次真实的最小模型检测并保存结果：须由操作者确认可能消耗额度，且候选已填写模型名；
    /// 仓储先把候选标记为检测中（正在检测时拒绝重复发起），检测结束后按 <see cref="UpstreamReport.CanActivate"/> 记为通过或失败。
    /// 取消令牌会传给检测过程；检测抛出异常时不会写回结果。
    /// </summary>
    public async Task<UpstreamReport> TestCandidate(string id, bool confirmed, CancellationToken cancellation)
    {
        if (!confirmed) throw new InvalidOperationException("请先确认最小模型测试可能消耗少量上游额度。");
        var candidate = ReadCandidate(id);
        if (candidate.Options.Model.Length == 0) throw new InvalidOperationException("请填写要实际调用的模型名称，再保存候选。");
        var secret = repository.BeginCandidateTest(id);
        // 网络等待不占用写事务，也不接触当前服务的暂停和冷却状态。
        var report = await probe.Run(new(candidate.ExpectedVersion, candidate.Options, Decrypt(secret)), cancellation);
        repository.FinishCandidateTest(id, report.CanActivate ? "passed" : "failed", JsonSerializer.Serialize(report, ConfigurationFiles.Json));
        return report;
    }

    /// <summary>把检测通过的候选原子地切换为新的活动版本；检测结果与活动版本的核对都在仓储的同一事务内完成。</summary>
    public void Activate(string id) => repository.Activate(id);

    /// <summary>按统一 JSON 选项反序列化存储的配置或报告；内容为 JSON null 时抛出 <see cref="InvalidOperationException"/>。</summary>
    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, ConfigurationFiles.Json) ?? throw new InvalidOperationException("上游配置损坏。");

    /// <summary>解密凭据并返回 UTF-8 字符串，解密出的字节缓冲在返回前清零。</summary>
    private string Decrypt(byte[] encrypted)
    {
        var plain = protector.Unprotect(encrypted);
        try { return Encoding.UTF8.GetString(plain); }
        // 无论解码是否成功都清零解密出的字节，缩短明文凭据在内存中的停留时间。
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
