namespace Core.Relay;

/// <summary>把可能带秘密、带控制字符的外来文字（上游错误说明、中转自己的提示）整理成能落库、能读出的一句：先脱敏，再截断，截断处不留半截秘密。</summary>
public static class SafeText
{
    /// <summary>
    /// 先用 <paramref name="redact"/> 去掉秘密，再把控制字符换成空格、去掉首尾空白，最后截到 <paramref name="limit"/> 字以内（截断时最后一字换成省略号，不劈开代理对）。
    /// 原文为空或全是空白时返回 null。
    /// </summary>
    public static string? Clip(string? text, Func<string, string> redact, int limit)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = new string(redact(text).Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (clean.Length <= limit) return clean;
        var cut = limit - 1;
        if (char.IsHighSurrogate(clean[cut - 1])) cut--;
        return clean[..cut] + "…";
    }
}
