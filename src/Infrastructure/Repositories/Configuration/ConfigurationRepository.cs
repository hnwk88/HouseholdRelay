using System.Text.Json;
using Application.Interfaces.Configuration;
using Core.Configuration;
using Infrastructure.Persistence;
using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Repositories.Configuration;

/// <summary>settings 表的读写：读取时校验设置，写入时按版本号乐观并发。</summary>
internal sealed class ConfigurationRepository(RelayDatabase database) : IConfigurationRepository
{
    /// <summary>读取并校验当前设置。</summary>
    public SettingsSnapshot Read()
    {
        using var connection = database.Open();
        return ReadSettings(connection, null);
    }

    /// <summary>版本号仍匹配时写入设置。</summary>
    public void Save(long version, RelayConfiguration config) => database.Write((connection, transaction) =>
    {
        SaveSettings(connection, transaction, version, config);
        return 0;
    });
}
