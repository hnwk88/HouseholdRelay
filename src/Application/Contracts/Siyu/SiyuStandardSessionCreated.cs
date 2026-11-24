namespace Application.Contracts.Siyu;

/// <summary>标准模式（版本 2）建会话的结果：会话视图与标准 API 接入；没有票据、没有身份，会话只用来续租、得知后端换了。</summary>
public sealed record SiyuStandardSessionCreated(SiyuSessionView Session, SiyuStandardAccess Standard) : SiyuSessionCreated(Session);
