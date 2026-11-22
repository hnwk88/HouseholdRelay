namespace Core.Certificates;

/// <summary>可展示的证书状态，不包含 PFX、私钥或密码。到期判断与提示读句只在这里写一份，概览、HTTPS 页与连接说明三页都读它。</summary>
public sealed record CertificateInfo(string Mode, string Host, DateTimeOffset ValidUntil, string Thumbprint, DateTimeOffset CheckedAt)
{
    /// <summary>到期前多少天起提示：30 天。</summary>
    public const int WarningDays = 30;

    /// <summary>此刻已经过期。</summary>
    /// <param name="now">此刻。</param>
    public bool Expired(DateTimeOffset now) => ValidUntil <= now;

    /// <summary>还没过期，但离到期不超过 <see cref="WarningDays"/> 天（第 30 天起提示，第 31 天不提示）。</summary>
    /// <param name="now">此刻。</param>
    public bool ExpiringSoon(DateTimeOffset now) => !Expired(now) && ValidUntil - now <= TimeSpan.FromDays(WarningDays);

    /// <summary>
    /// 快到期时的提示读句：到期日、还剩几天、下一步；没快到期（含已过期，已过期各页另有读句）时为空。
    /// 下一步按能不能重签分三种：存着私有 CA 私钥的重签（家人不用重装信任）；旧版建的私有 CA 只能新建（每台客户端重装一次信任）；导入的可信证书导入新的。
    /// </summary>
    /// <param name="now">此刻。</param>
    /// <param name="canReissue">存着私有 CA 私钥，可以只重签服务器证书。</param>
    public string? ExpiryWarning(DateTimeOffset now, bool canReissue) => ExpiringSoon(now)
        ? $"证书将在 {ValidUntil.ToLocalTime():yyyy-MM-dd} 到期，还剩 {Math.Max(1, (int)Math.Ceiling((ValidUntil - now).TotalDays))} 天。" + (canReissue
            ? "请在本机管理页「HTTPS 入口」按「用现有私有 CA 重签服务器证书」，家人不用重装信任。"
            : Mode == "private" ? "这份私有 CA 是旧版建的，私钥没有留存：请在本机管理页「HTTPS 入口」新建私有 CA，之后每台客户端要重新安装一次信任。"
            : "请在本机管理页「HTTPS 入口」导入新的证书。")
        : null;
}
