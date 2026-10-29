using Application.Interfaces.Administration;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Infrastructure.Security;

/// <summary>以 ASP.NET Core Identity 的 PBKDF2 密码哈希（21 万次迭代）实现管理密码哈希与校验。</summary>
internal sealed class AdministratorPasswordHasher : IAdministratorPasswordHasher
{
    private static readonly PasswordHasher<string> Hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = 210_000 }));

    /// <summary>计算密码哈希。</summary>
    public string Hash(string administratorId, string password) => Hasher.HashPassword(administratorId, password);

    /// <summary>校验密码；需要升级的哈希也视为通过。</summary>
    public bool Verify(string administratorId, string passwordHash, string password) =>
        Hasher.VerifyHashedPassword(administratorId, passwordHash, password) != PasswordVerificationResult.Failed;
}
