namespace Core.Siyu;

/// <summary>
/// 思域账户资料字段表的一行：字段名、允许的形状、是否必有、是否可为 null、变了是否算资料换代（<see cref="InProfileVersion"/> 为 false 的只是缓存时间，变了不结束会话）。
/// </summary>
public sealed record SiyuProfileField(string Name, SiyuProfileValueKind Kind, bool Required, bool Nullable, bool InProfileVersion);
