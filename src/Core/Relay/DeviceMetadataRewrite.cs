using System.Text;
using System.Text.Json;

namespace Core.Relay;

/// <summary>定位官方消息的唯一设备值，将解码位置映射回原始 JSON 字节；所有其他字节始终直接复制。</summary>
internal static class DeviceMetadataRewrite
{
    /// <summary>缺失、重复、损坏或未知设备格式时的固定读句；只说明本站支持范围，不冒充官方协议要求。</summary>
    private const string Unsupported = "This relay does not support this device metadata format for official account messages. This request was not sent.";

    /// <summary>按端点执行本站的官方请求合同，完整检查相关 JSON 后才拼接替换设备值。</summary>
    internal static byte[] Apply(string route, byte[] body, string deviceId)
    {
        if (route == "/v1/models")
        {
            if (body.Length != 0) throw new RelayFailure(400, "This relay supports official model requests only with an empty body.");
            return body;
        }
        if (route is not ("/v1/messages" or "/v1/messages/count_tokens"))
            throw new RelayFailure(400, "This relay does not support this official request endpoint.");
        try
        {
            var reader = new Utf8JsonReader(body);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw Rejected();
            var metadataSeen = false;
            var identifier = default(Identifier);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !reader.ValueTextEquals("metadata")) continue;
                if (metadataSeen) throw Rejected();
                metadataSeen = true;
                if (route == "/v1/messages/count_tokens")
                    throw new RelayFailure(400, "This relay does not support the metadata extension for official token counting requests.");
                identifier = ReadMetadata(ref reader);
            }
            if (route == "/v1/messages/count_tokens") return body;
            if (!metadataSeen) throw Rejected();
            return Rewrite(body, identifier, deviceId);
        }
        // 不把解析器里的正文片段或位置交给客户端；所有语法错误统一为本站格式不支持。
        catch (JsonException) { throw Rejected(); }
        // JSON 字符串含孤立代理项时 GetString 会拒绝解码，同样不暴露内部异常。
        catch (InvalidOperationException) { throw Rejected(); }
    }

    /// <summary>检查唯一 metadata 对象中的唯一字符串 user_id；读取到对象结尾，避免漏掉后置重复键。</summary>
    private static Identifier ReadMetadata(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw Rejected();
        var found = false;
        var identifier = default(Identifier);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 1) break;
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 2 || !reader.ValueTextEquals("user_id")) continue;
            if (found || !reader.Read() || reader.TokenType != JsonTokenType.String) throw Rejected();
            found = true;
            identifier = new((int)reader.TokenStartIndex + 1, reader.ValueSpan.Length, reader.GetString()!);
        }
        if (!found) throw Rejected();
        return identifier;
    }

    /// <summary>识别两种已支持的 user_id，只替换设备值对应的原始字节区间，保持内外层转义及其他内容。</summary>
    private static byte[] Rewrite(byte[] body, Identifier identifier, string deviceId)
    {
        var original = identifier.Value;
        if (string.IsNullOrWhiteSpace(original)) throw Rejected();
        var inner = Encoding.UTF8.GetBytes(original);
        int start, length;
        string current;
        if (DeviceMetadata.LegacyId().IsMatch(original))
        {
            start = "user_".Length; length = 64;
            current = original.Substring(start, length);
        }
        else
        {
            var reader = new Utf8JsonReader(inner);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw Rejected();
            var found = false;
            start = length = 0; current = "";
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !reader.ValueTextEquals("device_id")) continue;
                if (found || !reader.Read() || reader.TokenType != JsonTokenType.String) throw Rejected();
                found = true;
                start = (int)reader.TokenStartIndex + 1; length = reader.ValueSpan.Length; current = reader.GetString()!;
            }
            if (!found || string.IsNullOrWhiteSpace(current)) throw Rejected();
        }
        if (current == deviceId) return body;
        var raw = body.AsSpan(identifier.Start, identifier.Length);
        var rawStart = RawOffset(raw, start);
        var rawEnd = RawOffset(raw, start + length);
        var first = identifier.Start + rawStart;
        var after = identifier.Start + rawEnd;
        var replacement = Encoding.ASCII.GetBytes(deviceId);
        var result = new byte[body.Length - (after - first) + replacement.Length];
        body.AsSpan(0, first).CopyTo(result);
        replacement.CopyTo(result.AsSpan(first));
        body.AsSpan(after).CopyTo(result.AsSpan(first + replacement.Length));
        return result;
    }

    /// <summary>把解码后 UTF-8 的边界映射到原始 JSON 字符串内容；识别简单转义、Unicode 转义及代理对。</summary>
    private static int RawOffset(ReadOnlySpan<byte> raw, int decodedOffset)
    {
        var decoded = 0; var index = 0;
        while (decoded < decodedOffset && index < raw.Length)
        {
            if (raw[index] != (byte)'\\') { index++; decoded++; continue; }
            if (index + 1 >= raw.Length) throw Rejected();
            if (raw[index + 1] != (byte)'u') { index += 2; decoded++; continue; }
            var character = Hex(raw, index + 2);
            var consumed = 6;
            if (character is >= 0xd800 and <= 0xdbff)
            {
                if (index + 12 > raw.Length || raw[index + 6] != (byte)'\\' || raw[index + 7] != (byte)'u'
                    || Hex(raw, index + 8) is not (>= 0xdc00 and <= 0xdfff)) throw Rejected();
                consumed = 12; decoded += 4;
            }
            else
            {
                if (character is >= 0xdc00 and <= 0xdfff) throw Rejected();
                decoded += character <= 0x7f ? 1 : character <= 0x7ff ? 2 : 3;
            }
            index += consumed;
        }
        if (decoded != decodedOffset) throw Rejected();
        return index;
    }

    /// <summary>读取已验证 JSON 字符串里 Unicode 转义的四个十六进制数位；越界或非法值一律拒绝。</summary>
    private static int Hex(ReadOnlySpan<byte> raw, int start)
    {
        if (start + 4 > raw.Length) throw Rejected();
        var value = 0;
        foreach (var digit in raw.Slice(start, 4))
        {
            var number = digit is >= (byte)'0' and <= (byte)'9' ? digit - '0'
                : digit is >= (byte)'a' and <= (byte)'f' ? digit - 'a' + 10
                : digit is >= (byte)'A' and <= (byte)'F' ? digit - 'A' + 10 : -1;
            if (number < 0) throw Rejected();
            value = value * 16 + number;
        }
        return value;
    }

    /// <summary>返回本站格式拒绝，不标为上游认证失败或限流，也不包含输入正文。</summary>
    private static RelayFailure Rejected() => new(400, Unsupported);

    /// <summary>user_id 在原始正文中的字符串内容范围及解码值；范围不包括两端引号。</summary>
    private readonly record struct Identifier(int Start, int Length, string Value);
}
