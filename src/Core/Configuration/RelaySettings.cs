using System.Text.Json;
using Repositories;

namespace Core.Configuration;

/// <summary>
/// 实例设置（表 settings，主键固定为 1 的单行）：设置 JSON 与乐观并发版本号。读出经 <see cref="Snapshot"/> 校验；
/// 普通保存与证书保存都经 <see cref="Replace"/>：版本匹配且设备编号逐字相同才写；换设备编号只有站点维护的 <see cref="ReplaceDeviceId"/>。
/// </summary>
public sealed class RelaySettings : BaseEntity
{
    /// <summary>单行表唯一那一行的主键。</summary>
    public const long SingletonId = 1;

    /// <summary>设置版本号，每保存一次加一。</summary>
    public long Version { get; set; }

    /// <summary>设置 JSON（不含客户端名单：使用者与令牌单独存表）。</summary>
    public string Json { get; set; } = "";

    /// <summary>反序列化并校验当前设置，连同版本号返回；缺失或损坏的设备编号只报告恢复，不补造身份。</summary>
    public SettingsSnapshot Snapshot() => new(Version, Deserialize(Json));

    /// <summary>
    /// 普通配置与证书事务共同使用的写入边界：先校验新设置，再要求设备编号与库里逐字相同、版本仍是 <paramref name="expectedVersion"/>，然后写入并把版本加一。
    /// </summary>
    /// <param name="expectedVersion">页面或命令读到的版本。</param>
    /// <param name="config">新设置。</param>
    public void Replace(long expectedVersion, RelayConfiguration config)
    {
        config.Validate();
        var current = Deserialize(Json);
        if (!StringComparer.Ordinal.Equals(current.DeviceId, config.DeviceId))
            throw new InvalidOperationException("普通设置不能更换站点设备编号，请使用设备维护操作。");
        if (Version != expectedVersion) throw new InvalidOperationException("配置已被另一页面或命令修改，请重新加载页面再保存。");
        Json = Serialize(config); Version++;
    }

    /// <summary>站点维护换号：只把设备编号换成 <paramref name="deviceId"/>，其余设置原样，版本加一。生成新号是站点身份服务的事，这里只写。</summary>
    /// <param name="deviceId">已生成并校验过的新设备编号。</param>
    public void ReplaceDeviceId(string deviceId)
    {
        var value = Deserialize(Json);
        value.DeviceId = deviceId;
        Json = Serialize(value); Version++;
    }

    /// <summary>把配置序列化为设置 JSON：先深拷贝再清空客户端列表，因为用户和令牌哈希单独存表，不应重复写进设置。</summary>
    /// <param name="config">要保存的设置。</param>
    public static string Serialize(RelayConfiguration config)
    {
        var copy = JsonSerializer.Deserialize<RelayConfiguration>(JsonSerializer.Serialize(config, ConfigurationFiles.Json), ConfigurationFiles.Json)!;
        copy.Clients = [];
        return JsonSerializer.Serialize(copy, ConfigurationFiles.Json);
    }

    /// <summary>反序列化并校验设置；缺失或损坏时只给固定的恢复提示。</summary>
    /// <param name="json">设置 JSON。</param>
    public static RelayConfiguration Deserialize(string json)
    {
        try
        {
            var value = JsonSerializer.Deserialize<RelayConfiguration>(json, ConfigurationFiles.Json)
                ?? throw new InvalidOperationException("实例配置损坏，请恢复配套备份。");
            value.Validate();
            return value;
        }
        // JSON 异常可能带原始内容的位置；对外只提供固定的配置恢复提示。
        catch (JsonException) { throw new InvalidOperationException("实例配置损坏，请恢复配套备份。"); }
    }
}
