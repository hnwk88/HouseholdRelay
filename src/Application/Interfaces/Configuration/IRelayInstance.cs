namespace Application.Interfaces.Configuration;

/// <summary>当前数据目录对应的实例身份，用于数据保护密钥目录、Cookie 名称等按实例隔离的设置。</summary>
public interface IRelayInstance
{
    /// <summary>实例标记里记录的实例主键（32 位十六进制）。</summary>
    string InstanceId { get; }

    /// <summary>数据目录下受保护的状态文件夹（.state）的完整路径。</summary>
    string State { get; }
}
