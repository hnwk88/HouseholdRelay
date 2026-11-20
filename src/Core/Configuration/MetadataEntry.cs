using Repositories;

namespace Core.Configuration;

/// <summary>实例元数据的一项（表 metadata）：按键名（唯一）存一段文本，例如实例编号 instance_id、最近一次升级 last_upgrade、旧数据是否已导入 legacy_imported。</summary>
public sealed class MetadataEntry : BaseEntity
{
    /// <summary>键名。</summary>
    public string Key { get; set; } = "";

    /// <summary>值。</summary>
    public string Value { get; set; } = "";
}
