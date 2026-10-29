using Core.Configuration;

namespace Application.Interfaces.Configuration;

/// <summary>实例设置的持久化：读取并校验当前设置，按版本号做乐观并发写入。</summary>
public interface IConfigurationRepository
{
    /// <summary>读取当前设置及其版本号；设置缺失或损坏时抛出提示恢复备份的异常。</summary>
    SettingsSnapshot Read();

    /// <summary>仅当库中版本仍等于 <paramref name="version"/> 时写入设置并递增版本，否则提示重新加载。</summary>
    void Save(long version, RelayConfiguration config);
}
