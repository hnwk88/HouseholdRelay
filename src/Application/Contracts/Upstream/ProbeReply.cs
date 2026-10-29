using System.Net;

namespace Application.Contracts.Upstream;

/// <summary>诊断调用拿到的上游回复：状态码、有界正文以及是否为事件流。</summary>
public sealed record ProbeReply(HttpStatusCode Status, byte[] Body, bool Streaming);
