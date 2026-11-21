using System.Text.Json;
using System.Text.Json.Serialization;

namespace Core.Values;

/// <summary>按 <see cref="Wire{TEnum}"/> 的对照表读写 JSON：写出线上名；只接受线上名字符串，数字和不认识的字符串都拒绝并列出可选项。作字典键时同样处理。</summary>
public sealed class WireEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    /// <summary>读一个取值：必须是字符串且是声明过的线上名。</summary>
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException(Wire<TEnum>.Unknown(reader.TokenType.ToString()).Message);
        var text = reader.GetString();
        return Wire<TEnum>.TryParse(text, out var value) ? value : throw new JsonException(Wire<TEnum>.Unknown(text).Message);
    }

    /// <summary>写出线上名。</summary>
    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) => writer.WriteStringValue(Wire<TEnum>.Name(value));

    /// <summary>作字典键读：同样只认线上名。</summary>
    public override TEnum ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return Wire<TEnum>.TryParse(text, out var value) ? value : throw new JsonException(Wire<TEnum>.Unknown(text).Message);
    }

    /// <summary>作字典键写：写线上名。</summary>
    public override void WriteAsPropertyName(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) => writer.WritePropertyName(Wire<TEnum>.Name(value));
}
