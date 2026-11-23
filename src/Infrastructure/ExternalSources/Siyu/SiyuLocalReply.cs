using System.Text;
using System.Text.Json;

namespace Infrastructure.ExternalSources.Siyu;

/// <summary>
/// 思域在本地回的错误（官方没收到这次请求）：JSON 体形如 Anthropic 错误，<c>error.type</c> 固定为 <c>siyu_refused</c>，带原因码和中文读句。
/// 不带 Location，不带任何凭据或票据。
/// </summary>
public static class SiyuLocalReply
{
    /// <summary>没有专门读句时的通用句。</summary>
    public const string GenericSentence = "思域拒绝了这次请求，官方没有收到它。请重新启动思域后再试。";

    /// <summary>
    /// 生成完整回应字节（状态行、头、体）。<paramref name="close"/> 为真时带 <c>Connection: close</c>；
    /// <paramref name="proxyChallenge"/> 为真时带 <c>Proxy-Authenticate: Basic realm="siyu"</c>（外层 407 用）。
    /// </summary>
    public static byte[] Build(int status, string code, string? sentence, bool close, bool proxyChallenge = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        var body = JsonSerializer.SerializeToUtf8Bytes(new { type = "error", error = new { type = "siyu_refused", code, message = sentence ?? GenericSentence } });
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(' ').Append(Reason(status)).Append("\r\n");
        head.Append("Content-Type: application/json; charset=utf-8\r\n");
        head.Append("Cache-Control: no-store\r\n");
        if (proxyChallenge) head.Append("Proxy-Authenticate: Basic realm=\"siyu\"\r\n");
        head.Append("Content-Length: ").Append(body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");
        if (close) head.Append("Connection: close\r\n");
        head.Append("\r\n");
        return [.. Encoding.ASCII.GetBytes(head.ToString()), .. body];
    }

    /// <summary>写出本地回应并冲刷。</summary>
    public static async Task WriteAsync(Stream destination, int status, string code, string? sentence, bool close, CancellationToken cancellation, bool proxyChallenge = false)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await destination.WriteAsync(Build(status, code, sentence, close, proxyChallenge), cancellation);
        await destination.FlushAsync(cancellation);
    }

    /// <summary>常见状态码的原因短语；别的用 <c>Error</c>（短语不影响语义）。</summary>
    private static string Reason(int status) => status switch
    {
        400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed",
        407 => "Proxy Authentication Required", 409 => "Conflict", 411 => "Length Required", 413 => "Content Too Large", 417 => "Expectation Failed",
        426 => "Upgrade Required", 429 => "Too Many Requests", 431 => "Request Header Fields Too Large", 501 => "Not Implemented", 502 => "Bad Gateway",
        503 => "Service Unavailable", 505 => "HTTP Version Not Supported", _ => "Error",
    };
}
