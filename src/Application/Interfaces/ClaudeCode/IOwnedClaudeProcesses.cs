namespace Application.Interfaces.ClaudeCode;

/// <summary>本站启动的 Claude 进程的持久登记，供服务重启后确认旧写入者已经退出。</summary>
public interface IOwnedClaudeProcesses
{
    /// <summary>在启动子进程前持久记录 starting；未知进程状态不会因释放对象自动消失。</summary>
    IOwnedClaudeProcess Starting();

    /// <summary>准备配置前确认登记中的进程全部退出；活动、未知或损坏登记一律拒绝准备。</summary>
    void EnsureExited();
}
