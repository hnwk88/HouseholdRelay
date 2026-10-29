namespace Core.Upstream;

/// <summary>可向管理页面展示的候选；不含任何秘密。</summary>
public sealed record UpstreamCandidate(string Id, long ExpectedVersion, UpstreamOptions Options, string State, UpstreamReport? Report);
