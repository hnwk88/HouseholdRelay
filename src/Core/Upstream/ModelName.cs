using System.Diagnostics.CodeAnalysis;

namespace Core.Upstream;

/// <summary>
/// 「什么样的模型名合法」只在这里定义一次：1 到 <see cref="MaxLength"/> 个字符，只许 ASCII 字母、数字和 <c>- _ . / : @</c>。
/// 上游配置、检测读回的模型目录、转发记账、单价与用量筛选都调它，页面的 maxlength 也取 <see cref="MaxLength"/>；
/// 这样配得进上游的模型，转发时记得进用量、查得到单价。各处另有的检查（例如不许含凭据片段）是各自的事，叠加在它之上。
/// </summary>
public static class ModelName
{
    /// <summary>模型名最多这么多字符。</summary>
    public const int MaxLength = 200;

    /// <summary>读给管理员的规则说明。</summary>
    public const string Rule = "模型名称只能包含字母、数字和 - _ . / : @，最多 200 字符。";

    /// <summary>非空、不超过 <see cref="MaxLength"/>、每个字符都在白名单里。</summary>
    public static bool IsValid([NotNullWhen(true)] string? value) => value is { Length: > 0 and <= MaxLength } && value.All(Allowed);

    /// <summary>模型名里允许出现的字符：ASCII 字母、数字和 <c>- _ . / : @</c>。</summary>
    public static bool Allowed(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '@';
}
