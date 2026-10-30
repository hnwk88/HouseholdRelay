namespace Core.Upstream;

/// <summary>可向管理页面展示的已保存上游：主键、供应商名称（只做显示，关联一律走主键）、保存时基于的活动版本、配置、检测状态、最近一次报告与此刻能不能删除（由仓储按删除时的同一判据给出，页面只读它）；不含任何秘密。</summary>
public sealed record UpstreamCandidate(string Id, string Name, long ExpectedVersion, UpstreamOptions Options, string State, UpstreamReport? Report, bool Deletable);
