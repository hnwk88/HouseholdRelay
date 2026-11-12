using System.Security.Cryptography;
using Application.Interfaces.Configuration;

namespace Infrastructure.Repositories.Configuration;

/// <summary>站点编号唯一的生产生成实现；与账户、令牌、硬件、时间和地址无关。</summary>
internal sealed class RandomDeviceIdGenerator : IDeviceIdGenerator
{
    /// <summary>生成 32 字节密码学随机数，以 64 位小写十六进制返回。</summary>
    public string Generate() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}
