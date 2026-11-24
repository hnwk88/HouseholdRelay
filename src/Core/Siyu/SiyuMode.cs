namespace Core.Siyu;

/// <summary>思域会话的模式，由后端选定（版本 1 一律官方）：官方模式经思域代理到官方，标准模式走站点自己的标准 API（旧接法那一条）。</summary>
public enum SiyuMode
{
    /// <summary>官方账户：写身份、经思域代理、正文原样发给官方。</summary>
    Official,

    /// <summary>标准上游：客户端按标准 API 起进程，用个人令牌访问站点根地址，第三方密钥只在中转机。</summary>
    Standard,
}
