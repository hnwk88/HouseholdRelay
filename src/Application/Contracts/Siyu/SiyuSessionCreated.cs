namespace Application.Contracts.Siyu;

/// <summary>创建成功的完整结果：会话视图、启动身份、代理接入（含本次唯一票据）。同一 requestId 的重试原样返回它。官方令牌永不在这里。</summary>
public sealed record SiyuSessionCreated(SiyuSessionView Session, SiyuLaunchIdentity LaunchIdentity, SiyuProxyAccess Proxy);
