namespace Infrastructure.ExternalSources.Siyu;

/// <summary>头段里的一个字段：名字保留线上原样大小写；值是去掉两侧空白后的 ASCII 文本，<see cref="ValueStart"/>、<see cref="ValueLength"/> 指向原始头字节里这一段，替换时只动这一段。</summary>
public readonly record struct Http1Header(string Name, string Value, int ValueStart, int ValueLength)
{
    /// <summary>名字是否等于 <paramref name="name"/>（按 ASCII 不区分大小写）。</summary>
    public bool Is(string name) => string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);
}
