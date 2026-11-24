namespace Application.Contracts.Siyu;

/// <summary>
/// 建会话成功的结果，官方与标准是两件事、两个类型：<see cref="SiyuOfficialSessionCreated"/> 带启动身份与代理接入，<see cref="SiyuStandardSessionCreated"/> 只带标准 API 接入。
/// 同一 requestId 的重试原样返回它。官方令牌与第三方密钥永不在这里。
/// </summary>
public abstract record SiyuSessionCreated(SiyuSessionView Session);
