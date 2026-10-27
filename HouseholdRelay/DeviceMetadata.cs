using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HouseholdRelay;

public static partial class DeviceMetadata
{
    // Only recognized metadata.user_id formats are changed. Session IDs, account IDs,
    // prompts and signed attribution text are never rewritten.
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
        catch (JsonException) { return original; }
    }

    [GeneratedRegex("^user_[a-fA-F0-9]{64}(_account_[a-zA-Z0-9-]*_session_[a-zA-Z0-9-]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyId();
}
