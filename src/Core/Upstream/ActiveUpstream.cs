using System.Text.Json;
using Core.Configuration;
using Core.Values;
using Repositories;

namespace Core.Upstream;

/// <summary>
/// 活动上游（表 upstream，主键固定为 1 的单行）：版本、协议模式、地址、加密凭据、配置与报告 JSON、它来自哪一家已保存上游、自动切换开关。
/// 转发按它取在用的那一家；切换只经 <see cref="Activate"/>，版本号每切一次加一。
/// </summary>
public sealed class ActiveUpstream : BaseEntity
{
    /// <summary>单行表唯一那一行的主键。</summary>
    public const long SingletonId = 1;

    /// <summary>活动版本号；保存候选、切换、导入时都按它核对页面看到的是不是最新。</summary>
    public long Version { get; set; }

    /// <summary>协议模式（oauth、api-key 等）。</summary>
    public string Mode { get; set; } = "";

    /// <summary>上游根地址。</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>DPAPI 加密的凭据；新实例还没切换过时为空。</summary>
    public byte[]? Credential { get; set; }

    /// <summary>非秘密配置 JSON；版本 1 的旧行可能为空。</summary>
    public string? OptionsJson { get; set; }

    /// <summary>最近一次检测报告 JSON。</summary>
    public string? ReportJson { get; set; }

    /// <summary>它来自的已保存上游主键；新实例或旧版迁移没关联时为空。</summary>
    public long? UpstreamCandidateId { get; set; }

    /// <summary>自动切换开关：在用的那一家失败时是否按顺序换到其他测试通过的上游。新实例默认关，由管理员打开；库里存的值原样读出。</summary>
    public bool Failover { get; set; }

    /// <summary>页面或命令看到的版本还是不是此刻的活动版本；不是就抛出 <paramref name="message"/>。</summary>
    /// <param name="expectedVersion">调用方看到的版本。</param>
    /// <param name="message">版本已变时的读句。</param>
    public void EnsureVersion(long expectedVersion, string message)
    {
        if (Version != expectedVersion) throw new InvalidOperationException(message);
    }

    /// <summary>
    /// 把已核对过能切换（<see cref="UpstreamCandidate.EnsureActivatable"/>）的已保存上游写成新的活动版本：版本加一，模式、地址、凭据、配置、报告照它，记下它的主键。
    /// <paramref name="expectedVersion"/> 为空时按它保存时基于的版本核对；版本对不上就抛出「已被另一页面或命令更换」。
    /// </summary>
    /// <param name="candidate">要切换过去的已保存上游。</param>
    /// <param name="expectedVersion">调用方看到的活动版本。</param>
    public void Activate(UpstreamCandidate candidate, long? expectedVersion)
    {
        candidate.EnsureActivatable();
        EnsureVersion(expectedVersion ?? candidate.ExpectedVersion, "活动上游已被另一页面或命令更换，请重新加载页面后再切换。");
        var options = JsonSerializer.Deserialize<UpstreamOptions>(candidate.OptionsJson, ConfigurationFiles.Json) ?? throw new InvalidOperationException("上游配置损坏。");
        Version++; Mode = Wire<UpstreamMode>.Name(options.Mode); BaseUrl = options.BaseUrl; Credential = candidate.Credential;
        OptionsJson = candidate.OptionsJson; ReportJson = candidate.ReportJson; UpstreamCandidateId = candidate.Id;
    }
}
