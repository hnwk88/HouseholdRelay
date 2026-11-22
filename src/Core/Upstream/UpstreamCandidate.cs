using System.Text.Json;
using Core.Configuration;
using Repositories;

namespace Core.Upstream;

/// <summary>
/// 已保存上游（表 upstream_candidates 的一行）：对外编号、保存时基于的活动版本、配置与报告 JSON、加密凭据、检测状态与开始时刻、名称、导出搬走的时刻。
/// 主键由库生成，关联一律走它；创建时刻与停用（软删除）时刻用基类的 <see cref="BaseEntity.CreatedAt"/>、<see cref="BaseEntity.DeletedAt"/>。
/// 检测状态只经本类成对的方法改：<see cref="BeginTest"/> 进入检测中，<see cref="FinishTest"/> 写回结论，<see cref="ResetToUntested"/> 改了配置回到未测试。
/// </summary>
public sealed class UpstreamCandidate : BaseEntity
{
    /// <summary>检测超过这么多秒仍没结束视为已中断：可以重新检测，也可以删除。</summary>
    public const long TestWindowSeconds = 120;

    /// <summary>对外编号（32 位小写十六进制，保存前生成）；官方账户的账户目录按它命名，只交给官方分支，不承担任何关系。</summary>
    public string Uid { get; set; } = "";

    /// <summary>保存时基于的活动上游版本；激活时调用方没给期望版本就按它核对。</summary>
    public long ExpectedVersion { get; set; }

    /// <summary>非秘密配置（<see cref="UpstreamOptions"/>）的 JSON。</summary>
    public string OptionsJson { get; set; } = "";

    /// <summary>DPAPI 加密的凭据；停用后清成零长度占位。官方登录上游存加密的空串，令牌在账户目录里。</summary>
    public byte[] Credential { get; set; } = [];

    /// <summary>检测状态：untested、testing、passed、failed。</summary>
    public CandidateState State { get; set; } = CandidateState.Untested;

    /// <summary>最近一次检测报告（<see cref="UpstreamReport"/>）的 JSON；检测中或从没检测过为空。</summary>
    public string? ReportJson { get; set; }

    /// <summary>最近一次进入检测中的时刻（Unix 秒）；用来判断检测窗口是否已过。</summary>
    public long? TestStartedAt { get; set; }

    /// <summary>供应商名称，只做显示；旧版迁移来的可能是空串。</summary>
    public string Name { get; set; } = "";

    /// <summary>导出搬走的时刻（Unix 毫秒）；没导出过为空。导出过的官方账户本机不再用它转发、不再续期。</summary>
    public long? ExportedAt { get; set; }

    /// <summary>不在检测窗口内：不是检测中，或检测已开始超过 <see cref="TestWindowSeconds"/> 秒。</summary>
    /// <param name="nowSeconds">此刻（Unix 秒）。</param>
    public bool Idle(long nowSeconds) => State != CandidateState.Testing || TestStartedAt < nowSeconds - TestWindowSeconds;

    /// <summary>此刻能不能删除（停用）：不在检测窗口内，也不是活动上游的来源。列表里的「能不能删」与删除时用同一句。</summary>
    /// <param name="nowSeconds">此刻（Unix 秒）。</param>
    /// <param name="activeUpstreamCandidateId">活动上游来自的已保存上游主键，没有时为空。</param>
    public bool Deletable(long nowSeconds, long? activeUpstreamCandidateId) => Idle(nowSeconds) && Id != activeUpstreamCandidateId;

    /// <summary>进入检测中：清掉旧报告、记下开始时刻；已在检测窗口内时拒绝。与 <see cref="FinishTest"/> 成对。</summary>
    /// <param name="nowSeconds">此刻（Unix 秒）。</param>
    public void BeginTest(long nowSeconds)
    {
        if (!Idle(nowSeconds)) throw new InvalidOperationException("这个上游正在检测，请稍后刷新页面查看结果。");
        State = CandidateState.Testing; ReportJson = null; TestStartedAt = nowSeconds;
    }

    /// <summary>写回检测结论、报告与实际采用的配置。与 <see cref="BeginTest"/> 成对；获取模型时在用的那一家写回它原来的状态。</summary>
    /// <param name="state">检测结论或原状态。</param>
    /// <param name="reportJson">检测报告 JSON。</param>
    /// <param name="optionsJson">实际采用的配置 JSON。</param>
    public void FinishTest(CandidateState state, string reportJson, string optionsJson)
    {
        State = state; ReportJson = reportJson; OptionsJson = optionsJson;
    }

    /// <summary>此刻的检测阶段：不是检测中为空闲；检测中且在窗口内为正在检测；检测中却已过窗口为检测未完成。页面文字、删除与重新检测都读它。</summary>
    /// <param name="nowSeconds">此刻（Unix 秒）。</param>
    public UpstreamCheckPhase Phase(long nowSeconds) =>
        State != CandidateState.Testing ? UpstreamCheckPhase.Idle : Idle(nowSeconds) ? UpstreamCheckPhase.Interrupted : UpstreamCheckPhase.Checking;

    /// <summary>
    /// 检测没有走到结论就结束了（出错、被取消、服务重启）：回到未测试，报告读「检测未完成：原因。下一步」。与 <see cref="BeginTest"/> 成对；
    /// 只在仍是检测中时改，已经写回结论的不覆盖，返回是否改了。
    /// </summary>
    /// <param name="at">此刻。</param>
    /// <param name="reason">原因，见 <see cref="UpstreamReport"/> 上的几种读句。</param>
    public bool Interrupt(DateTimeOffset at, string reason)
    {
        if (State != CandidateState.Testing) return false;
        State = CandidateState.Untested; TestStartedAt = null;
        ReportJson = JsonSerializer.Serialize(UpstreamReport.Unfinished(at, reason), ConfigurationFiles.Json);
        return true;
    }

    /// <summary>改了配置（获取模型或改选模型）以后回到未测试，报告可为空；须重新测试才能切换。</summary>
    /// <param name="optionsJson">新配置 JSON。</param>
    /// <param name="reportJson">新报告 JSON，可为空。</param>
    public void ResetToUntested(string optionsJson, string? reportJson)
    {
        OptionsJson = optionsJson; ReportJson = reportJson; State = CandidateState.Untested;
    }

    /// <summary>记下导出搬走的时刻；已记过的保留最早那次。</summary>
    /// <param name="now">此刻。</param>
    public void MarkExported(DateTimeOffset now) => ExportedAt ??= now.ToUnixTimeMilliseconds();

    /// <summary>
    /// 停用前的判断与收尾：能删（<see cref="Deletable"/>）才清掉加密凭据，留零长度占位；不能删时抛出「刚被切换或开始检测」的读句。
    /// 行本身由调用方软删除，外键引用永远有效。
    /// </summary>
    /// <param name="nowSeconds">此刻（Unix 秒）。</param>
    /// <param name="activeUpstreamCandidateId">活动上游来自的已保存上游主键。</param>
    public void PrepareDeactivation(long nowSeconds, long? activeUpstreamCandidateId)
    {
        if (!Deletable(nowSeconds, activeUpstreamCandidateId))
            throw new InvalidOperationException("这个上游刚刚被切换或开始检测，没有删除。请重新加载页面再试。");
        Credential = [];
    }

    /// <summary>能不能切换过去：已导出搬走、没通过最小模型调用测试、报告不可激活时各自抛出原来的读句。</summary>
    public void EnsureActivatable()
    {
        if (ExportedAt is not null)
            throw new InvalidOperationException("这个官方账户已经导出搬走，本机不再用它，不能切换过去。要在本机继续用，请删除它后重新登录或导入登录文件。");
        if (State != CandidateState.Passed || ReportJson is null) throw NotTested();
        if (!(JsonSerializer.Deserialize<UpstreamReport>(ReportJson, ConfigurationFiles.Json) ?? throw new InvalidOperationException("上游配置损坏。")).CanActivate)
            throw new InvalidOperationException("这个上游的测试没有通过，不能切换。");
    }

    /// <summary>「还没有通过测试」的读句；要切换的那一家不存在时也读这一句。</summary>
    public static InvalidOperationException NotTested() => new("这个上游还没有通过最小模型调用测试，不能切换。请先测试。");
}
