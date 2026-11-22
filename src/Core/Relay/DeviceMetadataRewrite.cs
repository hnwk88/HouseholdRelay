using System.Text;
using System.Text.Json;

namespace Core.Relay;

/// <summary>定位官方消息的唯一设备值，将解码位置映射回原始 JSON 字节；所有其他字节始终直接复制。</summary>
internal static class DeviceMetadataRewrite
{
    /// <summary>缺失、重复、损坏或未知设备格式时的固定读句；只说明本站支持范围，不冒充官方协议要求。</summary>
    private const string Unsupported = "This relay does not support this device metadata format for official account messages. This request was not sent.";

    /// <summary>按端点执行本站的官方请求合同，完整检查相关 JSON 后才拼接替换设备值与账户编号；<paramref name="accountUuid"/> 为 null（上游没有账户目录）时账户段原样。</summary>
    internal static byte[] Apply(string route, byte[] body, string deviceId, string? accountUuid)
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
            return Rewrite(body, identifier, deviceId, accountUuid);
        }
        // 不把解析器里的正文片段或位置交给客户端；所有语法错误统一为本站格式不支持。
        catch (JsonException) { throw Rejected(); }
        // JSON 字符串含孤立代理项时 GetString 会拒绝解码，同样不暴露内部异常。
        catch (InvalidOperationException) { throw Rejected(); }
    }

    /// <summary>
    /// 只读核对 Messages 正文：与 <see cref="Apply"/> 同一套定位（唯一 metadata、唯一字符串 user_id、两种已支持格式），设备段等于 <paramref name="deviceId"/>、
    /// 账户段等于 <paramref name="accountUuid"/> 才算一致；不复制、不改写正文。缺失、重复、损坏、格式不认识一律 <see cref="DeviceMetadataCheck.Unsupported"/>。
    /// </summary>
    internal static DeviceMetadataCheck Check(ReadOnlySpan<byte> body, string deviceId, string accountUuid)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return DeviceMetadataCheck.Unsupported;
            Identifier? identifier = null;
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !reader.ValueTextEquals("metadata")) continue;
                if (identifier is not null) return DeviceMetadataCheck.Unsupported;
                identifier = ReadMetadata(ref reader);
            }
            if (identifier is not { } found || string.IsNullOrWhiteSpace(found.Value)) return DeviceMetadataCheck.Unsupported;
            var segments = Locate(found.Value, accountRequired: true);
            if (segments.Single(segment => segment.Kind == Part.Device).Current != deviceId) return DeviceMetadataCheck.DeviceDiffers;
            return segments.Single(segment => segment.Kind == Part.Account).Current == accountUuid ? DeviceMetadataCheck.Match : DeviceMetadataCheck.AccountDiffers;
        }
        // 定位规则判为不支持（与改写路径同一条规则）：只给结论，不抛给调用方。
        catch (RelayFailure) { return DeviceMetadataCheck.Unsupported; }
        // 语法错误：同上。
        catch (JsonException) { return DeviceMetadataCheck.Unsupported; }
        // 孤立代理项等解码失败：同上。
        catch (InvalidOperationException) { return DeviceMetadataCheck.Unsupported; }
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

    /// <summary>
    /// 识别两种已支持的 user_id，把设备段改成 <paramref name="deviceId"/>、账户段改成 <paramref name="accountUuid"/>（与原生直连的 oauthAccount.accountUuid 一致），
    /// 其余字节（会话段、内外层转义及所有其他内容）原样保留。两段现值都已等于目标时原数组返回；只按需替换，按原始字节偏移从后往前拼接，互不影响偏移。
    /// </summary>
    private static byte[] Rewrite(byte[] body, Identifier identifier, string deviceId, string? accountUuid)
    {
        var original = identifier.Value;
        if (string.IsNullOrWhiteSpace(original)) throw Rejected();
        var segments = Locate(original, accountUuid is not null);
        // 设备段必有值；账户段允许原来是空串（就地填入真实编号），没有真实编号时不动。只有现值与目标不同的段才替换。
        var edits = segments
            .Where(segment => segment.Kind == Part.Device || accountUuid is not null)
            .Select(segment => (segment.Start, segment.Length, Replacement: segment.Kind == Part.Device ? deviceId : accountUuid!, segment.Current))
            .Where(edit => edit.Current != edit.Replacement)
            .OrderByDescending(edit => edit.Start)
            .ToArray();
        if (edits.Length == 0) return body;
        // 偏移映射只认原始正文的设备标识区间；按解码位置从大到小替换，靠后的替换不移动靠前段的原始偏移。
        var raw = body.AsSpan(identifier.Start, identifier.Length);
        var result = body;
        foreach (var edit in edits)
        {
            var first = identifier.Start + RawOffset(raw, edit.Start);
            var after = identifier.Start + RawOffset(raw, edit.Start + edit.Length);
            var replacement = Encoding.ASCII.GetBytes(edit.Replacement);
            var next = new byte[result.Length - (after - first) + replacement.Length];
            result.AsSpan(0, first).CopyTo(next);
            replacement.CopyTo(next.AsSpan(first));
            result.AsSpan(after).CopyTo(next.AsSpan(first + replacement.Length));
            result = next;
        }
        return result;
    }

    /// <summary>设备段与账户段在 user_id 解码字符串上的区间与现值：旧格式按固定与分隔符定位，内嵌 JSON 格式按 device_id、account_uuid 两个顶层字符串字段定位；要填账户编号（<paramref name="accountRequired"/>）而账户段缺失时按格式不支持拒绝。</summary>
    private static (Part Kind, int Start, int Length, string Current)[] Locate(string original, bool accountRequired)
    {
        if (DeviceMetadata.LegacyId().IsMatch(original))
        {
            // user_{64位十六进制}_account_{账户}_session_{会话}：设备段固定 [5,64]，账户段在 _account_ 之后、_session_ 之前（可空）。
            const int deviceStart = 5, deviceLength = 64;
            var accountStart = deviceStart + deviceLength + "_account_".Length;
            var sessionAt = original.IndexOf("_session_", accountStart, StringComparison.Ordinal);
            if (sessionAt < 0) throw Rejected();
            return
            [
                (Part.Device, deviceStart, deviceLength, original.Substring(deviceStart, deviceLength)),
                (Part.Account, accountStart, sessionAt - accountStart, original.Substring(accountStart, sessionAt - accountStart))
            ];
        }
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(original));
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw Rejected();
        (int Start, int Length, string Value)? device = null, account = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0) break;
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
            var isDevice = reader.ValueTextEquals("device_id");
            var isAccount = reader.ValueTextEquals("account_uuid");
            if (!isDevice && !isAccount) continue;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String) throw Rejected();
            var located = ((int)reader.TokenStartIndex + 1, reader.ValueSpan.Length, reader.GetString()!);
            if (isDevice) { if (device is not null) throw Rejected(); device = located; }
            else { if (account is not null) throw Rejected(); account = located; }
        }
        if (device is not { Value.Length: > 0 } d || accountRequired && account is null) throw Rejected();
        return account is { } a
            ? [(Part.Device, d.Start, d.Length, d.Value), (Part.Account, a.Start, a.Length, a.Value)]
            : [(Part.Device, d.Start, d.Length, d.Value)];
    }

    /// <summary>user_id 里由本站用真实值改写的两段。</summary>
    private enum Part { Device, Account }

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
