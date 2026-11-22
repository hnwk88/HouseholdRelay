using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infrastructure.LocalIpc;

/// <summary>本机管理通道的共用约定：管道名按数据目录区分，一问一答各一行 UTF-8 JSON，单行有上限。</summary>
internal static class IdentityCommandPipe
{
    /// <summary>单行上限：请求与回应都很小，超出即当作坏数据。</summary>
    public const int MaxLineBytes = 8192;

    /// <summary>请求与回应的 JSON 约定（动作按名字写）。</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>按数据目录完整路径（不分大小写）得到管道名，同一台电脑上不同数据目录的实例互不相干。</summary>
    public static string Name(string stateDirectory) =>
        "HouseholdRelay-identity-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(stateDirectory).ToUpperInvariant())))[..32];

    /// <summary>写一行 JSON 并刷出。</summary>
    public static async Task WriteLine<T>(Stream stream, T value, CancellationToken cancellation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        await stream.WriteAsync(bytes, cancellation);
        await stream.WriteAsync("\n"u8.ToArray(), cancellation);
        await stream.FlushAsync(cancellation);
    }

    /// <summary>读到换行为止的一行并解析；超长、提前断开或不是合法 JSON 时返回 default。</summary>
    public static async Task<T?> ReadLine<T>(Stream stream, CancellationToken cancellation)
    {
        var buffer = new byte[MaxLineBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellation);
            if (read == 0) return default;
            if (buffer[length] == (byte)'\n')
            {
                try { return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length), Json); }
                // 对端写来的不是约定的 JSON：当作坏数据，不转述内容。
                catch (JsonException) { return default; }
            }
            length++;
        }
        return default;
    }
}
