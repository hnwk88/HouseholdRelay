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

    /// <summary>（版本 1）身份代次过时（409）时服务端当前的站点身份代次，供 MyCoAI 重新准备；其余拒绝与版本 2 为 null。</summary>
    public long? CurrentIdentityVersion { get; init; }

    /// <summary>（版本 2）可自动处理时的下一步；版本 1 与要人处理的拒绝为 null。</summary>
    public SiyuRemedy? Remedy { get; init; }

    /// <summary>
    /// 按调用方的协议版本给出线上形状：版本 1 原样返回自己；版本 2 查 <see cref="SiyuRemedies"/>，可自动处理就换成带打回的一份，
    /// 身份代次只放进打回（不再另带 <see cref="CurrentIdentityVersion"/>，一个值一个来源）；要人处理的也去掉 <see cref="CurrentIdentityVersion"/>。
    /// </summary>
    public SiyuRefusal ForProtocol(int clientProtocolVersion)
    {
        if (!SiyuContract.BackendChooses(clientProtocolVersion) || Remedy is not null) return this;
        var remedy = SiyuRemedies.For(Code, CurrentIdentityVersion);
        return remedy is null && CurrentIdentityVersion is null ? this : new SiyuRefusal(Status, Code, Sentence) { Remedy = remedy };
    }
}
