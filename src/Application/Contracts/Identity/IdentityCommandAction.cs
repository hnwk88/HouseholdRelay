namespace Application.Contracts.Identity;

/// <summary>命令行交给运行中服务执行的使用者管理动作。</summary>
public enum IdentityCommandAction
{
    /// <summary>新建使用者并签发第一版令牌。</summary>
    Add,

    /// <summary>轮换令牌。</summary>
    Rotate,

    /// <summary>启用。</summary>
    Enable,

    /// <summary>停用。</summary>
    Disable,

    /// <summary>改显示名。</summary>
    Rename,
}
