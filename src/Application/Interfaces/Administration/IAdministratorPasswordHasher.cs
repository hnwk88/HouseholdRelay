namespace Application.Interfaces.Administration;

/// <summary>管理密码的加盐慢哈希与校验；具体算法由基础设施层提供。</summary>
public interface IAdministratorPasswordHasher
{
    /// <summary>为指定管理员主键计算密码哈希。</summary>
    string Hash(string administratorId, string password);

    /// <summary>校验密码；哈希需要升级也视为通过。</summary>
    bool Verify(string administratorId, string passwordHash, string password);
}
