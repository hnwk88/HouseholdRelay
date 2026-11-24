namespace Core.Siyu;

/// <summary>打回告诉客户端的下一步；线上名字见 <see cref="SiyuRemedies.Wire"/>。</summary>
public enum SiyuRemedyAction
{
    /// <summary>结束本地会话，用新的 requestId 重新建会话，按新回应准备再起进程。</summary>
    Recreate,

    /// <summary>过 <see cref="SiyuRemedy.RetryAfterSeconds"/> 秒再建会话。</summary>
    Wait,
}
