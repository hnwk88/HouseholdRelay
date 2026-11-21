namespace Core.Configuration;

/// <summary>一个整数设置项允许的闭区间：校验用 <see cref="Contains"/>，页面的 min、max 属性取 <see cref="Min"/>、<see cref="Max"/>。</summary>
/// <param name="Min">最小值（含）。</param>
/// <param name="Max">最大值（含）。</param>
public readonly record struct SettingRange(int Min, int Max)
{
    /// <summary><paramref name="value"/> 是否在区间内。</summary>
    public bool Contains(int value) => value >= Min && value <= Max;
}
