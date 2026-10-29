namespace Core.Upstream;

/// <summary>
/// 「获取模型」的结果：<paramref name="Status"/> 为 <c>passed</c>（拿到目录）、<c>unsupported</c>（上游不提供目录）、
/// <c>rejected</c>（可试的认证方式都被拒）、<c>cooldown</c>（上游限流）或 <c>failed</c>（连接或协议失败）；
/// <paramref name="Authentication"/> 是被接受的认证方式，没确定时为空；<paramref name="Message"/> 是读给管理员的一句话，不含秘密。
/// </summary>
public sealed record ModelCatalog(string Status, string? Authentication, string[] Models, string Message);
