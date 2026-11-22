namespace Core.Siyu;

/// <summary>
/// 思域在本地拒绝一次发送（官方收不到这次请求）：<see cref="Status"/> 是回给客户端的状态码，<see cref="Code"/> 是固定英文原因码，
/// <see cref="Sentence"/> 是给使用者听的中文读句（含下一步）。会话失效、令牌失效、维护、版本不支持、凭据需要重新登录都用它。
/// </summary>
public sealed class SiyuRefusal : Exception
{
    /// <summary>三样都必填。</summary>
    public SiyuRefusal(int status, string code, string sentence) : base(sentence)
    {
        ArgumentException.ThrowIfNullOrEmpty(code); ArgumentException.ThrowIfNullOrEmpty(sentence);
        Status = status; Code = code; Sentence = sentence;
    }

    /// <summary>回给客户端的 HTTP 状态码。</summary>
    public int Status { get; }

    /// <summary>固定原因码。</summary>
    public string Code { get; }

    /// <summary>中文读句。</summary>
    public string Sentence { get; }

    /// <summary>身份代次过时（409）时服务端当前的站点身份代次，供 MyCoAI 重新准备；其余拒绝为 null。</summary>
    public long? CurrentIdentityVersion { get; init; }
}
