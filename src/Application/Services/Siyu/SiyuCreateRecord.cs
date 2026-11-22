using Application.Contracts.Siyu;

namespace Application.Services.Siyu;

/// <summary>一次成功创建的幂等记录：原请求（比对重试是否逐项相同）、原结果（重试原样返回）、保留到何时。</summary>
internal sealed record SiyuCreateRecord(SiyuCreateRequest Request, SiyuSessionCreated Created, DateTimeOffset ExpiresAt);
