namespace Application.Interfaces.Configuration;

/// <summary>仅供全新站点建立和显式身份维护使用的设备编号生成入口。</summary>
public interface IDeviceIdGenerator
{
    /// <summary>生成一个独立的 32 字节密码学随机设备编号。</summary>
    string Generate();
}
