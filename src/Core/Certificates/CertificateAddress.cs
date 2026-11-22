namespace Core.Certificates;

/// <summary>证书保存时一起写进设置的对外地址与本机监听端口；只为退回上一份证书时把地址一起退回。不含秘密。</summary>
public sealed record CertificateAddress(string PublicUrl, int ListenPort);
