namespace Application.Interfaces.Configuration;

/// <summary>判断一次存取失败是不是「库正被另一个连接占着写锁」：这是数据库提供程序的细节，由基础设施层按具体数据库的错误码实现，应用层只拿它读出「库正忙」的读句。</summary>
public interface IStorageBusy
{
    /// <summary><paramref name="error"/>（或它包着的内层异常）是不是库正忙、等写锁超时。</summary>
    /// <param name="error">存取时抛出的异常。</param>
    bool IsBusy(Exception error);
}
