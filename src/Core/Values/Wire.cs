using System.Reflection;
using System.Text.Json.Serialization;

namespace Core.Values;

/// <summary>
/// 封闭取值在库里、JSON 里、表单里写成的那串字符（线上名）。每个取值都用 <see cref="JsonStringEnumMemberNameAttribute"/> 标注它今天存的字符串，
/// 这里是唯一的对照表：EF 的值转换、JSON 转换器、表单与查询参数的解析都只经它。读到不认识的字符串时拒绝，并列出可选项，不兜底。
/// </summary>
public static class Wire<TEnum> where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> _names = [];
    private static readonly Dictionary<string, TEnum> _values = new(StringComparer.Ordinal);

    /// <summary>按枚举声明顺序列出全部线上名。</summary>
    public static IReadOnlyList<string> Names { get; }

    /// <summary>按声明顺序列出全部取值。</summary>
    public static IReadOnlyList<TEnum> Values { get; }

    /// <summary>读一遍枚举的每个取值和它的标注；有取值没标注时直接抛出，类型加载即失败，不会带着缺口运行。</summary>
    static Wire()
    {
        var names = new List<string>(); var values = new List<TEnum>();
        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var name = field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
                ?? throw new InvalidOperationException($"{typeof(TEnum).Name}.{field.Name} 没有标注线上名。");
            var value = (TEnum)field.GetValue(null)!;
            _names.Add(value, name); _values.Add(name, value); names.Add(name); values.Add(value);
        }
        Names = names; Values = values;
    }

    /// <summary>取值的线上名；不是声明过的取值时抛出。</summary>
    public static string Name(TEnum value) =>
        _names.TryGetValue(value, out var name) ? name : throw new InvalidOperationException($"{typeof(TEnum).Name} 没有取值 {value}。");

    /// <summary>按线上名（区分大小写）解析；不认识时抛出 <see cref="InvalidOperationException"/>，消息列出全部可选项。</summary>
    public static TEnum Parse(string? text) => TryParse(text, out var value) ? value : throw Unknown(text);

    /// <summary>按线上名（区分大小写）解析；不认识时返回 false。</summary>
    public static bool TryParse(string? text, out TEnum value)
    {
        if (text is not null && _values.TryGetValue(text, out value)) return true;
        value = default; return false;
    }

    /// <summary>「不认识的取值」的读句：哪种取值、读到了什么、可选哪些。</summary>
    public static InvalidOperationException Unknown(string? text) =>
        new($"不认识的{typeof(TEnum).Name}取值「{text}」，可选：{string.Join("、", Names)}。");
}
