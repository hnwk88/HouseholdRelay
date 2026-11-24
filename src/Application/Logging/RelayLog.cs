using Core.Relay;
using Microsoft.Extensions.Logging;

namespace Application.Logging;

/// <summary>
/// 本工程自己写的几种日志事件，全部只带四样：异常类型链（<see cref="ExceptionChain.Types"/>）、阶段、请求编号、上游主键。
/// 不传异常对象、不带消息正文：消息里可能有令牌、凭据、路径或代理地址，任何提供程序（文件、服务窗口）都只拿到这四样。
/// </summary>
public static partial class RelayLog
{
    /// <summary>兜底处接住了没有预料到的异常：记下类型链、阶段与请求编号（没有时为空），再给调用方通用读句。</summary>
    /// <param name="logger">日志。</param>
    /// <param name="error">接住的异常；只取类型链。</param>
    /// <param name="stage">阶段，例如 relay、management、command:serve、serve:certificate、upstream-check。</param>
    /// <param name="requestId">中转请求编号或管理页请求编号；命令行没有时为空。</param>
    /// <param name="upstreamCandidateId">涉及的已保存上游主键；没有时为空。</param>
    public static void Unexpected(ILogger logger, Exception error, string stage, string? requestId = null, long? upstreamCandidateId = null) =>
        UnexpectedRecord(logger, ExceptionChain.Types(error), stage, requestId, upstreamCandidateId);

    /// <summary>已保存上游的配置或报告读不懂，被跳过或读作「配置已损坏」：只记类型链、阶段与上游主键。</summary>
    /// <param name="logger">日志。</param>
    /// <param name="error">解析异常；只取类型链。</param>
    /// <param name="stage">阶段，例如 upstream-config:standby。</param>
    /// <param name="upstreamCandidateId">那一家的主键。</param>
    public static void DamagedUpstream(ILogger logger, Exception error, string stage, long upstreamCandidateId) =>
        DamagedUpstreamRecord(logger, ExceptionChain.Types(error), stage, upstreamCandidateId);

    /// <summary>思域给版本 2 客户端打回了一次（留痕）：记使用者主键、令牌主键、会话编号（建会话时为空）、原因码、下一步与给出的身份代次；不记令牌、票据、邮箱或资料原文。</summary>
    /// <param name="logger">日志。</param>
    /// <param name="userId">使用者主键。</param>
    /// <param name="tokenId">个人令牌主键。</param>
    /// <param name="sessionId">思域会话编号；建会话时为空。</param>
    /// <param name="code">原因码。</param>
    /// <param name="remedy">下一步的线上名字（recreate、wait）。</param>
    /// <param name="identityVersion">打回给出的期望身份代次；没有时为空。</param>
    public static void Remedy(ILogger logger, long userId, long tokenId, string? sessionId, string code, string remedy, long? identityVersion) =>
        RemedyRecord(logger, userId, tokenId, sessionId, code, remedy, identityVersion);

    /// <summary>打回事件的模板；参数名就是文件日志认的白名单字段名。</summary>
    [LoggerMessage(EventId = 9003, EventName = "SiyuRemedy", Level = LogLevel.Warning,
        Message = "Siyu remedy {Remedy} for {Code}; user {UserId}; token {TokenId}; session {SessionId}; identity version {IdentityVersion}")]
    private static partial void RemedyRecord(ILogger logger, long userId, long tokenId, string? sessionId, string code, string remedy, long? identityVersion);

    /// <summary>兜底事件的模板；参数名就是文件日志认的白名单字段名。</summary>
    [LoggerMessage(EventId = 9001, EventName = "Unexpected", Level = LogLevel.Error,
        Message = "Unexpected {ExceptionType} at {Stage}; request {RequestId}; upstream {UpstreamCandidateId}")]
    private static partial void UnexpectedRecord(ILogger logger, string exceptionType, string stage, string? requestId, long? upstreamCandidateId);

    /// <summary>配置损坏事件的模板。</summary>
    [LoggerMessage(EventId = 9002, EventName = "DamagedUpstream", Level = LogLevel.Warning,
        Message = "Damaged upstream configuration {ExceptionType} at {Stage}; upstream {UpstreamCandidateId}")]
    private static partial void DamagedUpstreamRecord(ILogger logger, string exceptionType, string stage, long upstreamCandidateId);
}
