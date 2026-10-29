using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Core.Relay;

/// <summary>账户模式下把请求体中可识别的 metadata.user_id 设备段统一为本机设备标识，其余字节原样保留。</summary>
public static partial class DeviceMetadata
{
    // Only recognized metadata.user_id formats are changed. Session IDs, account IDs,
    // prompts and signed attribution text are never rewritten.
    /// <summary>
    /// 在请求体 JSON 中查找顶层 <c>metadata.user_id</c>，若其格式可识别则只把其中的设备段改为 <paramref name="deviceId"/>，
    /// 并在原字节上就地拼接替换值，其余字节（包括格式与字段顺序）保持不变。
    /// 找不到、值不是字符串或格式不认识时原样返回同一数组；在找到该字段前遇到非法 JSON 时读取器会抛出 <see cref="JsonException"/>，由调用方处理。
    /// </summary>
    public static byte[] Normalize(byte[] body, string deviceId)
    {
        var reader = new Utf8JsonReader(body);
        var inMetadata = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
            {
                inMetadata = reader.ValueTextEquals("metadata");
                continue;
            }
            if (!inMetadata || reader.CurrentDepth != 2 || reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("user_id")) continue;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String) return body;
            var original = reader.GetString()!;
            var replacement = NormalizeId(original, deviceId);
            if (replacement == original) return body;
            var start = (int)reader.TokenStartIndex; var end = (int)reader.BytesConsumed;
            var encoded = JsonSerializer.SerializeToUtf8Bytes(replacement);
            var result = new byte[body.Length - (end - start) + encoded.Length];
            body.AsSpan(0, start).CopyTo(result); encoded.CopyTo(result.AsSpan(start));
            body.AsSpan(end).CopyTo(result.AsSpan(start + encoded.Length)); return result;
        }
        return body;
    }

    /// <summary>
    /// 改写单个 user_id：旧格式 <c>user_{64位十六进制}_account_…_session_…</c> 只替换十六进制设备段；
    /// 以 <c>{</c> 开头的 JSON 对象只替换字符串类型的 <c>device_id</c> 字段；其他格式或解析失败时原样返回。
    /// </summary>
    private static string NormalizeId(string original, string deviceId)
    {
        var match = LegacyId().Match(original);
        if (match.Success) return "user_" + deviceId + match.Groups[1].Value;
        if (!original.StartsWith('{')) return original;
        try
        {
            if (JsonNode.Parse(original) is not JsonObject value
                || value["device_id"] is not JsonValue old || !old.TryGetValue<string>(out _)) return original;
            value["device_id"] = deviceId; return value.ToJsonString();
        }
        // 看起来像 JSON 但解析失败时不改写也不报错，保证无法识别的 user_id 原样透传、不让请求因此失败。
        catch (JsonException) { return original; }
    }

    /// <summary>匹配旧格式 user_id：<c>user_</c> 加 64 位十六进制设备段，第 1 组捕获其后的 account 与 session 部分以便原样保留。</summary>
    [GeneratedRegex("^user_[a-fA-F0-9]{64}(_account_[a-zA-Z0-9-]*_session_[a-zA-Z0-9-]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyId();
}
