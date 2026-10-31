using Core.Upstream;

namespace Application.Contracts.Upstream;

/// <summary>
/// 导出文件里的一家上游：供应商名称、非秘密配置，以及二选一的秘密——API 密钥或贴进来的账户令牌放在 <see cref="Credential"/>，
/// 登录得到的官方账户放它账户目录里的 <c>.credentials.json</c> 原文 <see cref="LoginFile"/>。只出现在加密文件里，不出现在页面、日志和库里。
/// </summary>
public sealed record ExportedUpstream(string Name, UpstreamOptions Options, string? Credential, byte[]? LoginFile);
