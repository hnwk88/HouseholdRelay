using Repositories;

namespace Core.Usage;

/// <summary>
/// 一次中转请求的用量记录（表 usage_requests）：请求编号（转发前生成的 GUID，唯一，日志、协议诊断与个人用量页都认它）、归属的使用者与令牌、
/// 开始与结束时刻（UTC 刻度）、接口类别、模型、结果、计数、单价快照与金额、记账的那一家已保存上游及其名称快照。主键由库生成，上游尝试按它挂在下面。
/// 状态只经本类的方法推进：<see cref="Dispatch"/> 发送、<see cref="Complete"/> 结束、<see cref="Interrupt"/> 启动时把遗留记录标成中断；都只对仍处于开始状态的记录生效。
/// </summary>
public sealed class UsageRequest : BaseEntity
{
    /// <summary>请求编号（32 位十六进制 GUID）。</summary>
    public string RequestId { get; set; } = "";

    /// <summary>归属的使用者主键。</summary>
    public long UserId { get; set; }

    /// <summary>归属的令牌主键。</summary>
    public long TokenId { get; set; }

    /// <summary>发送时的活动上游版本；开始时先记当时的版本，发送时改成实际用的版本。</summary>
    public long? UpstreamVersion { get; set; }

    /// <summary>开始时刻（UTC 刻度）。</summary>
    public long RequestStartedAt { get; set; }

    /// <summary>结束时刻（UTC 刻度）；还没结束为空。</summary>
    public long? EndedAt { get; set; }

    /// <summary>接口类别：inference、estimate、models。</summary>
    public UsageKind Kind { get; set; }

    /// <summary>请求的模型名；模型查询等没有模型时为空。</summary>
    public string? Model { get; set; }

    /// <summary>结果：started、succeeded、failed、cancelled、interrupted。</summary>
    public UsageOutcome Outcome { get; set; } = UsageOutcome.Started;

    /// <summary>是否已发往上游。</summary>
    public bool Dispatched { get; set; }

    /// <summary>回给客户端的状态码。</summary>
    public int? StatusCode { get; set; }

    /// <summary>上游回的状态码。</summary>
    public int? UpstreamStatus { get; set; }

    /// <summary>总耗时（毫秒）。</summary>
    public long? DurationMs { get; set; }

    /// <summary>用量完整性：complete、partial、not_provided、not_applicable。</summary>
    public UsageCompleteness Completeness { get; set; } = UsageCompleteness.Partial;

    /// <summary>输入计数。</summary>
    public long? InputTokens { get; set; }

    /// <summary>输出计数。</summary>
    public long? OutputTokens { get; set; }

    /// <summary>缓存读取计数。</summary>
    public long? CacheReadTokens { get; set; }

    /// <summary>缓存写入计数。</summary>
    public long? CacheWriteTokens { get; set; }

    /// <summary>发送时复制的单价快照（<see cref="UsagePrice"/> 的 JSON）；之后改价不改变历史金额。</summary>
    public string? PriceJson { get; set; }

    /// <summary>金额；未知时为空。</summary>
    public decimal? Amount { get; set; }

    /// <summary>币种。</summary>
    public string? Currency { get; set; }

    /// <summary>金额来源：reported、estimated、not_applicable、unknown。</summary>
    public MoneySource MoneySource { get; set; } = MoneySource.Unknown;

    /// <summary>金额完整性：complete、partial、not_applicable、unknown。</summary>
    public MoneyCompleteness MoneyCompleteness { get; set; } = MoneyCompleteness.Unknown;

    /// <summary>记账的那一家已保存上游主键；不是从已保存上游发出的为空。</summary>
    public long? UpstreamCandidateId { get; set; }

    /// <summary>记账那一刻那一家的名称快照，只做显示。</summary>
    public string? UpstreamName { get; set; }

    /// <summary>仍处于开始状态。</summary>
    public bool IsStarted => Outcome == UsageOutcome.Started;

    /// <summary>新建一条开始记录；归属必须来自已鉴权的使用者与令牌，内存夹具没有主键时抛出，按存储故障处理、请求不发出。</summary>
    /// <param name="requestId">请求编号。</param>
    /// <param name="userId">使用者主键。</param>
    /// <param name="tokenId">令牌主键。</param>
    /// <param name="upstreamVersion">此刻的活动上游版本。</param>
    /// <param name="startedAtTicks">开始时刻（UTC 刻度）。</param>
    /// <param name="kind">接口类别。</param>
    public static UsageRequest Start(string requestId, long? userId, long? tokenId, long? upstreamVersion, long startedAtTicks, UsageKind kind) => new()
    {
        RequestId = requestId,
        UserId = userId ?? throw new InvalidOperationException("Usage owner unavailable."),
        TokenId = tokenId ?? throw new InvalidOperationException("Usage owner unavailable."),
        UpstreamVersion = upstreamVersion, RequestStartedAt = startedAtTicks, Kind = kind,
    };

    /// <summary>标记已发送：记下实际用的活动版本、首先要试的那一家、模型、它此刻的单价快照与名称快照。已不在开始状态时什么都不改，返回 false。</summary>
    /// <param name="version">活动上游版本。</param>
    /// <param name="upstreamCandidateId">首先要试的那一家。</param>
    /// <param name="model">模型名。</param>
    /// <param name="priceJson">那一家这个模型此刻的单价 JSON。</param>
    /// <param name="upstreamName">那一家此刻的名称。</param>
    public bool Dispatch(long version, long? upstreamCandidateId, string? model, string? priceJson, string? upstreamName)
    {
        if (!IsStarted) return false;
        UpstreamVersion = version; UpstreamCandidateId = upstreamCandidateId; Model = model; Dispatched = true;
        PriceJson = priceJson; UpstreamName = upstreamName;
        return true;
    }

    /// <summary>这次用量该记在哪一家：有尝试时记最后一次尝试实际服务的那一家（自动切换换了家就改记那一家），没有尝试时沿用发送时记的那一家。</summary>
    /// <param name="result">结束结果。</param>
    public long? ServedBy(UsageEnd result) => result.Attempts.Count > 0 ? result.Attempts[^1].UpstreamCandidateId : UpstreamCandidateId;

    /// <summary>
    /// 写入结束结果：结果、计数、金额与按 <see cref="ServedBy"/> 定下的那一家（换了家时连同那一家的单价快照与名称快照一起换）。
    /// 已不在开始状态时什么都不改并返回 false，重复的结束通知不覆盖首次结果。
    /// </summary>
    /// <param name="endedAtTicks">结束时刻（UTC 刻度）。</param>
    /// <param name="result">结束结果。</param>
    /// <param name="money">按单价快照算好的金额。</param>
    /// <param name="servedBy">记账的那一家。</param>
    /// <param name="priceJson">记账用的单价快照。</param>
    /// <param name="upstreamName">那一家的名称快照。</param>
    public bool Complete(long endedAtTicks, UsageEnd result, UsageMoney money, long? servedBy, string? priceJson, string? upstreamName)
    {
        if (!IsStarted) return false;
        EndedAt = endedAtTicks; Outcome = result.Outcome; Dispatched = result.Dispatched; StatusCode = result.Status;
        UpstreamStatus = result.UpstreamStatus; DurationMs = result.DurationMs; Completeness = result.Completeness;
        InputTokens = result.Tokens.Input; OutputTokens = result.Tokens.Output; CacheReadTokens = result.Tokens.CacheRead; CacheWriteTokens = result.Tokens.CacheWrite;
        Amount = money.Amount; Currency = money.Currency; MoneySource = money.Source; MoneyCompleteness = money.Completeness;
        PriceJson = priceJson; UpstreamName = upstreamName; UpstreamCandidateId = servedBy;
        return true;
    }

    /// <summary>服务启动时把上次遗留的开始记录标成中断：用量与金额都只算部分，结束时刻记此刻。</summary>
    /// <param name="nowTicks">此刻（UTC 刻度）。</param>
    public void Interrupt(long nowTicks)
    {
        if (!IsStarted) return;
        Outcome = UsageOutcome.Interrupted; Completeness = UsageCompleteness.Partial; MoneyCompleteness = MoneyCompleteness.Partial; EndedAt = nowTicks;
    }
}
