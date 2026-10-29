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
        using var command = Command(connection, null, "SELECT version,json FROM settings WHERE id=1");
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("实例配置缺失，请恢复备份。");
        var config = JsonSerializer.Deserialize<RelayConfiguration>(reader.GetString(1), ConfigurationFiles.Json)
            ?? throw new InvalidOperationException("实例配置损坏。");
        config.Validate();
        return new(reader.GetInt64(0), config);
    }

    /// <summary>版本号仍匹配时写入设置。</summary>
    public void Save(long version, RelayConfiguration config) => database.Write((connection, transaction) =>
    {
        if (Execute(connection, transaction, "UPDATE settings SET version=version+1,json=$json WHERE id=1 AND version=$version",
            ("$json", SerializeSettings(config)), ("$version", version)) != 1)
            throw new InvalidOperationException("配置已被另一页面或命令修改，请重新加载页面再保存。");
        return 0;
    });
}
