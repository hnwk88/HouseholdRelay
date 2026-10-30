using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using Core.Relay;
using Core.Upstream;

namespace Core.Usage;

/// <summary>
/// 一次请求对上游的一次尝试：哪家（候选主键，名称只做显示）、第几次、到响应头与总耗时、上游状态码与请求编号、错误类型与脱敏截断后的说明、
/// 连接失败分类和去向。转发时边走边记，结束时随用量结束记录在同一个短事务里落库；不含提示词、回答、上游凭据或个人令牌。
/// </summary>
public sealed class UpstreamAttempt(int number, string? candidateId, long version, DateTimeOffset startedAt)
{
    /// <summary>错误说明落库的最大字符数（含截断时补的省略号）。</summary>
    public const int MessageLimit = 300;
    private readonly long started = Stopwatch.GetTimestamp();

    /// <summary>本请求里的第几次尝试，从 1 开始。</summary>
    public int Number { get; } = number;
    /// <summary>这次尝试所用已保存上游的主键；活动上游不是从已保存上游切换来的（新实例、独立中转）时为 null。</summary>
    public string? CandidateId { get; } = candidateId;
    /// <summary>这次尝试所用的活动上游版本。</summary>
    public long Version { get; } = version;
    /// <summary>开始尝试的 UTC 时刻，取自用量时钟。</summary>
    public DateTimeOffset StartedAt { get; } = startedAt;
    /// <summary>从开始尝试到收到上游响应头的毫秒数；没收到响应头时为 null。</summary>
    public long? HeadersMs { get; private set; }
    /// <summary>从开始尝试到读完响应或失败的毫秒数。</summary>
    public long DurationMs { get; private set; }
    /// <summary>上游返回的状态码；连不上或没收到响应头时为 null。</summary>
    public int? UpstreamStatus { get; private set; }
    /// <summary>上游响应头里的请求编号（request-id 或 x-request-id），格式不合或含秘密时为 null。</summary>
    public string? UpstreamRequestId { get; private set; }
    /// <summary>错误类型：上游给的 error.type 原值，或本中转的分类（unreadable、unrecognized、connection_failed、timeout、interrupted、protocol、cancelled、relay_error）；成功时为 null。</summary>
    public string? ErrorType { get; private set; }
    /// <summary>上游给的 error.message，已去掉上游凭据与个人令牌并截到 <see cref="MessageLimit"/> 字以内；没有时为 null。</summary>
    public string? ErrorMessage { get; private set; }
    /// <summary>连接失败分类：dns、refused、tls、connect_timeout、proxy、other；不是连接失败时为 null。</summary>
    public string? ConnectFailure { get; private set; }
    /// <summary>这次尝试的去向：client（结果交给客户端）或 next（换下一家）。</summary>
    public string Disposition { get; private set; } = "client";

    /// <summary>收到上游响应头：记下到响应头耗时、状态码，以及格式合规且不含秘密的上游请求编号。</summary>
    public void Headers(int status, string? requestId, Func<string, string> redact)
    {
        HeadersMs = Elapsed; UpstreamStatus = status;
        UpstreamRequestId = requestId is { Length: > 0 and <= 200 } && requestId.All(c => c is > ' ' and < (char)127) && redact(requestId) == requestId ? requestId : null;
    }

    /// <summary>响应已完整转给客户端：记下总耗时；上游回了非 2xx 或流里出现错误事件时，从用量累加器取错误类型与说明，格式不认识时记 unrecognized。</summary>
    public void Completed(int status, UsageAccumulator usage, Func<string, string> redact)
    {
        DurationMs = Elapsed;
        if (status is >= 200 and < 300 && !usage.Error) return;
        ErrorType = usage.ErrorType ?? "unrecognized"; ErrorMessage = Clip(usage.ErrorMessage, redact);
    }

    /// <summary>尝试途中抛出异常：记下总耗时，并按原始异常分出错误类型与连接失败分类；<paramref name="clientAborted"/> 表示客户端已经断开。</summary>
    public void Failed(Exception error, bool clientAborted, Func<string, string> redact)
    {
        DurationMs = Elapsed;
        (ErrorType, ConnectFailure) = error switch
        {
            OperationCanceledException when clientAborted => ("cancelled", null),
            _ when Find<UpstreamProxyUnavailableException>(error) is not null => ("connection_failed", "proxy"),
            // SocketsHttpHandler 的建连超时（含 TLS 握手）抛出带 TimeoutException 的取消；中转自己的总超时不带。
            OperationCanceledException when Find<TimeoutException>(error) is not null => ("connection_failed", "connect_timeout"),
            OperationCanceledException => ("timeout", null),
            HttpRequestException request => ("connection_failed", Classify(request)),
            IOException => ("interrupted", null),
            RelayFailure => ("protocol", null),
            _ => ("relay_error", null)
        };
        // 中转自己的固定提示（跳转、内容编码、超限）可以原样记；其他异常的消息可能带地址或底层细节，不记。
        ErrorMessage = error is RelayFailure failure ? Clip(failure.Message, redact) : null;
    }

    /// <summary>从 HTTP 建连异常链里分出域名解析、拒绝连接、TLS、建连超时，其余归「其他」。</summary>
    private static string Classify(HttpRequestException error)
    {
        if (error.HttpRequestError == HttpRequestError.SecureConnectionError || Find<AuthenticationException>(error) is not null) return "tls";
        if (error.HttpRequestError == HttpRequestError.NameResolutionError) return "dns";
        return Find<SocketException>(error)?.SocketErrorCode switch
        {
            SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "dns",
            SocketError.ConnectionRefused => "refused",
            SocketError.TimedOut => "connect_timeout",
            _ => "other"
        };
    }

    /// <summary>沿内部异常链找第一个指定类型的异常。</summary>
    private static T? Find<T>(Exception error) where T : Exception
    {
        for (Exception? inner = error; inner is not null; inner = inner.InnerException) if (inner is T match) return match;
        return null;
    }

    /// <summary>先去掉秘密，再把控制字符换成空格，最后截到 <see cref="MessageLimit"/> 字以内；先脱敏后截断，截断处不会留下半截秘密。</summary>
    private static string? Clip(string? message, Func<string, string> redact)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var text = new string(redact(message).Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (text.Length <= MessageLimit) return text;
        var cut = MessageLimit - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + "…";
    }

    /// <summary>从开始尝试到此刻的毫秒数。</summary>
    private long Elapsed => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
