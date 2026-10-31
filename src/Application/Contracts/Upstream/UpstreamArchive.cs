namespace Application.Contracts.Upstream;

/// <summary>导出文件解密后的正文：按上游页顺序排列的上游；格式版本号在加密信封的文件头里，不在这里。</summary>
public sealed record UpstreamArchive(IReadOnlyList<ExportedUpstream> Upstreams);
