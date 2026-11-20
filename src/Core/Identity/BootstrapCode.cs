using Repositories;

namespace Core.Identity;

/// <summary>一次性首次设置码（表 bootstrap，主键固定为 1 的单行）：哈希与 DPAPI 密文。首次设置完成即物理删除。</summary>
public sealed class BootstrapCode : BaseEntity
{
    /// <summary>单行表唯一那一行的主键。</summary>
    public const long SingletonId = 1;

    /// <summary>设置码的十六进制哈希。</summary>
    public string Hash { get; set; } = "";

    /// <summary>设置码的 DPAPI 密文，本机启动窗口解密显示。</summary>
    public byte[] Protected { get; set; } = [];
}
