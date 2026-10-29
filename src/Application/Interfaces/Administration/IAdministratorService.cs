namespace Application.Interfaces.Administration;

/// <summary>本机一次性引导和可撤销管理会话，与个人中转令牌完全分离。</summary>
public interface IAdministratorService
{
    /// <summary>是否已完成首次设置。</summary>
    bool IsInitialized { get; }

    /// <summary>解密并返回一次性引导码，只在本机启动窗口显示；设置完成后为空。</summary>
    string? BootstrapCode();

    /// <summary>用引导码完成首次设置并保存管理密码。</summary>
    void Initialize(string code, string password);

    /// <summary>校验管理密码并签发 8 小时会话；密码错误时返回空。</summary>
    string? Login(string password);

    /// <summary>会话是否有效。</summary>
    bool IsSessionValid(string? session);

    /// <summary>撤销会话。</summary>
    void Logout(string? session);
}
