namespace Application.Contracts.Upstream;

/// <summary>一次导出的结果：加密好的文件、导出了哪几家（按上游页顺序的名称）、其中随之搬走的官方账户名称；名称只做读出。</summary>
public sealed record UpstreamExport(byte[] File, IReadOnlyList<string> Names, IReadOnlyList<string> Moved);
