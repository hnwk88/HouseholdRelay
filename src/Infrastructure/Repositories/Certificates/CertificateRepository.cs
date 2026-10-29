using System.Text.Json;
using Application.Interfaces.Certificates;
using Core.Certificates;
using Core.Configuration;
using Infrastructure.Persistence;
using static Infrastructure.Persistence.RelayDatabase;

namespace Infrastructure.Repositories.Certificates;

/// <summary>secrets 中的服务器证书与 public_assets 中的展示信息、私有 CA 的读写；保存证书时同事务更新设置。</summary>
internal sealed class CertificateRepository(RelayDatabase database) : ICertificateRepository
{
    /// <summary>读取加密的服务器证书。</summary>
    public byte[]? ReadProtectedCertificate()
    { using var connection = database.Open(); return Scalar(connection, null, "SELECT protected FROM secrets WHERE name='tls'") as byte[]; }

    /// <summary>读取已记录的证书展示信息。</summary>
    public CertificateInfo? ReadStoredInfo()
    {
        using var connection = database.Open();
        var json = Scalar(connection, null, "SELECT value FROM public_assets WHERE name='tls_info'") as string;
        return json is null ? null : JsonSerializer.Deserialize<CertificateInfo>(json, ConfigurationFiles.Json);
    }

    /// <summary>读取私有 CA 公共 PEM。</summary>
    public string? ReadAuthorityPem()
    {
        using var connection = database.Open();
        return Scalar(connection, null, "SELECT value FROM public_assets WHERE name='legacy_ca'") as string;
    }

    /// <summary>保存导入的可信证书并删除旧私有 CA。</summary>
    public void SaveImported(long settingsVersion, RelayConfiguration config, byte[] protectedPfx, CertificateInfo info) => database.Write((connection, transaction) =>
    {
        if (Execute(connection, transaction, "UPDATE settings SET version=version+1,json=$json WHERE id=1 AND version=$version",
            ("$json", SerializeSettings(config)), ("$version", settingsVersion)) != 1)
            throw new InvalidOperationException("项目设置已经改变，请重新加载再导入证书。");
        Execute(connection, transaction, "INSERT INTO secrets VALUES('tls',$value) ON CONFLICT(name) DO UPDATE SET protected=excluded.protected", ("$value", protectedPfx));
        Execute(connection, transaction, "INSERT INTO public_assets VALUES('tls_info',$value) ON CONFLICT(name) DO UPDATE SET value=excluded.value",
            ("$value", JsonSerializer.Serialize(info, ConfigurationFiles.Json)));
        return Execute(connection, transaction, "DELETE FROM public_assets WHERE name='legacy_ca'");
    });

    /// <summary>保存新签发的私有证书与私有 CA 公共 PEM。</summary>
    public void SavePrivate(long settingsVersion, RelayConfiguration config, byte[] protectedPfx, CertificateInfo info, string authorityPem) => database.Write((connection, transaction) =>
    {
        if (Execute(connection, transaction, "UPDATE settings SET version=version+1,json=$json WHERE id=1 AND version=$version",
            ("$json", SerializeSettings(config)), ("$version", settingsVersion)) != 1)
            throw new InvalidOperationException("项目设置已经改变，请重新加载后创建证书。");
        Execute(connection, transaction, "INSERT INTO secrets VALUES('tls',$value) ON CONFLICT(name) DO UPDATE SET protected=excluded.protected", ("$value", protectedPfx));
        Execute(connection, transaction, "INSERT INTO public_assets VALUES('tls_info',$value) ON CONFLICT(name) DO UPDATE SET value=excluded.value",
            ("$value", JsonSerializer.Serialize(info, ConfigurationFiles.Json)));
        return Execute(connection, transaction, "INSERT INTO public_assets VALUES('legacy_ca',$value) ON CONFLICT(name) DO UPDATE SET value=excluded.value", ("$value", authorityPem));
    });
}
