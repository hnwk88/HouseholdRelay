using System.Text.Json;

namespace Core.Siyu;

/// <summary>
/// 官方 <c>GET /v1/mcp_servers</c> 成功回应的只读解析：形如 <c>{"data":[{"type":"mcp_server","id":"…",…}],"has_more":…,"next_page":…}</c>
/// （线索：openclaude <c>services/mcp/claudeai.ts</c> 的 <c>ClaudeAIMcpServersResponse</c>）。只取每一项的 <c>id</c>，别的字段不读不存；
/// 发现项里的第三方 URL 只供客户端原生语义使用，不成为可代理目的地。
/// </summary>
public static class McpServerList
{
    /// <summary>一次发现最多认这么多个连接器；超出说明回应不是预期的形状，整份不认。</summary>
    public const int MaxConnectors = 1000;

    /// <summary>
    /// 从解码后的回应正文读出连接器编号。顶层不是对象、没有 <c>data</c> 数组、某一项不是对象或没有合法 <c>id</c>（与连接器路径同一字符集与长度）、
    /// 项数超上限、JSON 不合法时返回 false：这份回应不进发现集合，连接器请求照旧按「没发现过」处理。
    /// </summary>
    public static bool TryReadIds(ReadOnlySpan<byte> json, out IReadOnlyList<string> ids)
    {
        ids = [];
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 16 });
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() > MaxConnectors) return false;
            var found = new List<string>(data.GetArrayLength());
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                    || id.GetString() is not { } value || !IsConnectorId(value)) return false;
                found.Add(value);
            }
            ids = found;
            return true;
        }
        // 不是合法 JSON：这份回应不认，不抛给转发路径（回应照样原样转给客户端）。
        catch (JsonException) { return false; }
    }

    /// <summary>连接器编号的字符集与长度，与 <see cref="SiyuRoutes"/> 对连接器路径的要求一致。</summary>
    public static bool IsConnectorId(string value) => value.Length is > 0 and <= SiyuRoutes.MaxConnectorIdLength
        && value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');
}
