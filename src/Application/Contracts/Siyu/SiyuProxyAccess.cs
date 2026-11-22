namespace Application.Contracts.Siyu;

/// <summary>
/// 思域代理接入：<see cref="Endpoint"/> 是不含凭据的代理地址；外层 CONNECT 用 Basic「会话编号:票据」认证；
/// <see cref="Ticket"/> 是本会话唯一的临时秘密，只在创建结果里出现，不落库、不写日志；代理 CA 的公钥证书 PEM 与 SHA256 指纹只给思域进程信任。
/// </summary>
public sealed record SiyuProxyAccess(string Endpoint, string Authentication, string Ticket, string AuthorityPem, string AuthoritySha256);
