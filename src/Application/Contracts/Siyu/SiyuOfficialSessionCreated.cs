namespace Application.Contracts.Siyu;

/// <summary>官方模式建会话的完整结果：会话视图、启动身份、代理接入（含本次唯一票据）。版本 1 只有这一种。</summary>
public sealed record SiyuOfficialSessionCreated(SiyuSessionView Session, SiyuLaunchIdentity LaunchIdentity, SiyuProxyAccess Proxy) : SiyuSessionCreated(Session);
