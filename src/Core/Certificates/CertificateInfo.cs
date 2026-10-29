namespace Core.Certificates;

/// <summary>可展示的证书状态，不包含 PFX、私钥或密码。</summary>
public sealed record CertificateInfo(string Mode, string Host, DateTimeOffset ValidUntil, string Thumbprint, DateTimeOffset CheckedAt);
