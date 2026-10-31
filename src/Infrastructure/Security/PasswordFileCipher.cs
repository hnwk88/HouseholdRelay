using System.Security.Cryptography;
using System.Text;
using Application.Interfaces.Security;

namespace Infrastructure.Security;

/// <summary>
/// 上游导出文件的加密信封：魔数 <c>HRUPSTREAM</c>、格式版本 1、16 字节盐、12 字节随机数、32 字节密码校验值、16 字节标签，后接密文。
/// 密钥由 <see cref="Rfc2898DeriveBytes.Pbkdf2(byte[], byte[], int, HashAlgorithmName, int)"/>（SHA-256，60 万次）从密码派生 64 字节：前 32 字节作 <see cref="AesGcm"/> 密钥，后 32 字节作校验值。
/// 文件头（魔数到校验值）作为附加数据参与认证，改头里任何一个字节都解不开。校验值只用来把「密码不对」和「文件被改过」分开读出：猜密码的代价与直接试解密相同，不削弱强度。
/// </summary>
internal sealed class PasswordFileCipher : IPasswordCipher
{
    private static readonly byte[] Magic = "HRUPSTREAM"u8.ToArray();
    private const byte Version = 1;
    private const int SaltBytes = 16, NonceBytes = 12, CheckBytes = 32, TagBytes = 16, KeyBytes = 32, Iterations = 600_000;
    /// <summary>文件头长度：魔数、版本号、盐、随机数、校验值；标签紧跟其后。</summary>
    private static readonly int HeaderBytes = Magic.Length + 1 + SaltBytes + NonceBytes + CheckBytes;

    /// <summary>生成随机盐与随机数，派生密钥后加密；派生出的密钥与密码字节用完清零。</summary>
    public byte[] Seal(byte[] plain, string password)
    {
        var file = new byte[HeaderBytes + TagBytes + plain.Length];
        Magic.CopyTo(file, 0); file[Magic.Length] = Version;
        RandomNumberGenerator.Fill(file.AsSpan(Magic.Length + 1, SaltBytes + NonceBytes));
        var keys = Derive(password, file.AsSpan(Magic.Length + 1, SaltBytes));
        try
        {
            keys.AsSpan(KeyBytes).CopyTo(file.AsSpan(HeaderBytes - CheckBytes, CheckBytes));
            using var aes = new AesGcm(keys.AsSpan(0, KeyBytes), TagBytes);
            aes.Encrypt(file.AsSpan(Magic.Length + 1 + SaltBytes, NonceBytes), plain, file.AsSpan(HeaderBytes + TagBytes), file.AsSpan(HeaderBytes, TagBytes), file.AsSpan(0, HeaderBytes));
            return file;
        }
        // 派生出的密钥无论加密成败都清零。
        finally { CryptographicOperations.ZeroMemory(keys); }
    }

    /// <summary>依次核对魔数、版本号、长度、密码校验值，最后解密并验标签；任何一步不过都只读出原因，验不过时已写出的明文清零、不交出去。</summary>
    public byte[] Open(byte[] file, string password)
    {
        if (file.Length < Magic.Length + 1 || !file.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidOperationException("选的文件不是上游导出文件。请选旧电脑上按「导出加密文件」下载的那个文件。");
        if (file[Magic.Length] != Version)
            throw new InvalidOperationException($"这个导出文件的格式版本是 {file[Magic.Length]}，本程序只认版本 {Version}。请把两边的中转升级到同一版本，再导出、导入一次。");
        if (file.Length < HeaderBytes + TagBytes)
            throw new InvalidOperationException("导出文件不完整，可能没有拷完。请回旧电脑重新导出一份，再导入。");
        var keys = Derive(password, file.AsSpan(Magic.Length + 1, SaltBytes));
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(keys.AsSpan(KeyBytes), file.AsSpan(HeaderBytes - CheckBytes, CheckBytes)))
                throw new InvalidOperationException("导出密码不对。请输入导出时设的那个密码再试；确认密码没错的话，这个文件可能被改过，请回旧电脑重新导出一份。");
            var plain = new byte[file.Length - HeaderBytes - TagBytes];
            using var aes = new AesGcm(keys.AsSpan(0, KeyBytes), TagBytes);
            try { aes.Decrypt(file.AsSpan(Magic.Length + 1 + SaltBytes, NonceBytes), file.AsSpan(HeaderBytes + TagBytes), file.AsSpan(HeaderBytes, TagBytes), plain, file.AsSpan(0, HeaderBytes)); }
            // 密码对、内容却验不过：文件被改过或没拷完整；已写出的半截明文清零，不交给调用方。
            catch (AuthenticationTagMismatchException)
            {
                CryptographicOperations.ZeroMemory(plain);
                throw new InvalidOperationException("导出文件被改过或不完整：密码是对的，但内容校验没有通过。请回旧电脑重新导出一份，再导入。");
            }
            return plain;
        }
        // 派生出的密钥无论解密成败都清零。
        finally { CryptographicOperations.ZeroMemory(keys); }
    }

    /// <summary>按盐从密码派生 64 字节（加密密钥 + 校验值）；密码的 UTF-8 字节用完清零。</summary>
    private static byte[] Derive(string password, ReadOnlySpan<byte> salt)
    {
        var secret = Encoding.UTF8.GetBytes(password);
        try { return Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, KeyBytes + CheckBytes); }
        // 密码字节只用于派生，派生完立即清零。
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
}
