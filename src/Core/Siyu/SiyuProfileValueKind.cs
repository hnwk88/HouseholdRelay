namespace Core.Siyu;

/// <summary>思域账户资料字段表里每个字段允许的 JSON 形状；取值来自中转机上真实 oauthAccount 的只读类型快照。</summary>
public enum SiyuProfileValueKind
{
    /// <summary>小写连字符格式的 UUID 字符串。</summary>
    Uuid,

    /// <summary>任意字符串。</summary>
    Text,

    /// <summary>带日期与时刻的 ISO 8601 字符串。</summary>
    Timestamp,

    /// <summary>true 或 false。</summary>
    Boolean,

    /// <summary>JSON 数字。</summary>
    Number,

    /// <summary>JSON 对象，内容原样保留不解释。</summary>
    Object,

    /// <summary>只见过 null，非空时的类型机制未明：接受任意标量原样保留，拒绝对象和数组。</summary>
    UnverifiedScalar,
}
