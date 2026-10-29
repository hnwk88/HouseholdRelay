using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Core.Configuration;

/// <summary>配置 JSON 格式、令牌生成与哈希的统一规则，以及旧版配置文件的原子读写。</summary>
public static class ConfigurationFiles
{
    /// <summary>全工程共用的配置 JSON 选项：Web 默认（驼峰命名、大小写不敏感读取）并缩进输出；共享实例，调用方不得再修改。</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>计算令牌的 SHA-256 并返回 64 位小写十六进制；库里只存这个哈希，不存令牌明文。</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>生成新的个人令牌：<c>hr_</c> 前缀加 32 字节密码学随机数的 URL 安全 Base64（去掉填充），便于识别且可直接放进请求头。</summary>
    public static string NewToken() => "hr_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>按 <see cref="Json"/> 读取并反序列化配置文件；内容为 JSON <c>null</c> 时抛出 <see cref="InvalidOperationException"/>，文件不存在等 IO 异常原样上抛。</summary>
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
        ?? throw new InvalidOperationException("配置文件为空。");

    /// <summary>先写入同目录的 <c>.tmp</c> 临时文件（无 BOM 的 UTF-8，末尾带换行），再覆盖式移动到目标路径，避免中途崩溃留下半截配置；不做跨进程加锁。</summary>
    public static void Write<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json) + Environment.NewLine, new UTF8Encoding(false));
        // 用整文件替换代替原地改写：目标文件要么是旧内容要么是完整新内容；若写临时文件时失败，.tmp 会残留，下次写入时直接覆盖。
        File.Move(temp, path, true);
    }
}
