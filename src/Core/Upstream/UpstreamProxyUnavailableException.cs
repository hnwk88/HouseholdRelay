namespace Core.Upstream;

/// <summary>代理已启用却取不到可用地址时抛出：一律拒绝发出请求，绝不悄悄直连；消息是读给管理员的原因与下一步。</summary>
public sealed class UpstreamProxyUnavailableException(string message) : InvalidOperationException(message);
