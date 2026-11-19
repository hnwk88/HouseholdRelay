using Application.Interfaces.Administration;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Infrastructure.Security;

/// <summary>以 ASP.NET Core Identity 的 PBKDF2 密码哈希（21 万次迭代）实现管理密码哈希与校验。</summary>
internal sealed class AdministratorPasswordHasher : IAdministratorPasswordHasher
{
    private static readonly PasswordHasher<string> Hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = 210_000 }));

    /// <summary>计算密码哈希。<see cref="PasswordHasher{TUser}"/> 的 user 参数只为扩展留着、算法不读它（结果里只有格式标记、盐与迭代参数），这里传空串；版本 10 之前按管理员主键算的旧哈希照常校验。</summary>
    public string Hash(string password) => Hasher.HashPassword("", password);

    /// <summary>校验密码；需要升级的哈希也视为通过。</summary>
    public bool Verify(string passwordHash, string password) =>
        Hasher.VerifyHashedPassword("", passwordHash, password) != PasswordVerificationResult.Failed;
}
