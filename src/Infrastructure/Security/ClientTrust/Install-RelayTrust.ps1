# 在当前 Windows 用户范围安装公共 CA，并为新启动的 Claude Code 配置额外信任。
[CmdletBinding()]
param([Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedSha256)
$ErrorActionPreference = 'Stop'
$relayCaPath = Join-Path $PSScriptRoot 'relay-ca.pem'
$relayPem = [IO.File]::ReadAllText($relayCaPath)
if ($relayPem.Contains('PRIVATE KEY')) { throw '信任包不得包含私钥。' }
$relayCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($relayCaPath)
$relayHash = [Security.Cryptography.SHA256]::Create()
try {
    $relayFingerprint = [BitConverter]::ToString($relayHash.ComputeHash($relayCertificate.RawData)).Replace('-', '')
    if ($relayFingerprint -ne $ExpectedSha256) { throw 'CA 指纹与管理员提供的指纹不一致，未安装。' }
    $relayConstraints = $relayCertificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' }
    if (-not $relayConstraints.CertificateAuthority -or $relayCertificate.HasPrivateKey -or
        $relayCertificate.NotBefore -gt (Get-Date) -or $relayCertificate.NotAfter -le (Get-Date)) {
        throw '证书不是有效的公共 CA，未安装。'
    }
    $relayDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('HouseholdRelay\trust\' + $relayFingerprint)
    $relayCombinedPath = Join-Path $relayDirectory 'claude-ca.pem'
    $relayPrevious = [Environment]::GetEnvironmentVariable('NODE_EXTRA_CA_CERTS', 'User')
    $relayExisting = $relayPrevious
    if (-not $relayExisting) { $relayExisting = $env:NODE_EXTRA_CA_CERTS }
    $relayCombined = $relayPem
    if ($relayExisting -and $relayExisting -ne $relayCombinedPath) {
        if (-not [IO.File]::Exists($relayExisting)) { throw '现有 NODE_EXTRA_CA_CERTS 文件不存在，请先修复原信任配置。' }
        $relayCombined = [IO.File]::ReadAllText($relayExisting) + "`n" + $relayPem
    } elseif ([IO.File]::Exists($relayCombinedPath)) { $relayCombined = [IO.File]::ReadAllText($relayCombinedPath) }
    [IO.Directory]::CreateDirectory($relayDirectory) | Out-Null
    [IO.File]::WriteAllText($relayCombinedPath, $relayCombined, [Text.Encoding]::ASCII)
    $relayStore = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'CurrentUser')
    $relayStore.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try {
        $relayAlreadyTrusted = $relayStore.Certificates.Find('FindByThumbprint', $relayCertificate.Thumbprint, $false).Count -gt 0
        $relayReceipt = Join-Path $relayDirectory 'installation.json'
        if (-not [IO.File]::Exists($relayReceipt)) {
            @{ previous_user_ca = $relayPrevious; previously_trusted = $relayAlreadyTrusted; thumbprint = $relayCertificate.Thumbprint } |
                ConvertTo-Json | Set-Content -LiteralPath $relayReceipt -Encoding UTF8
        }
        try {
            Write-Output ('已核对 CA SHA-256：' + $relayFingerprint)
            Write-Output ('Windows 安全警告中的 SHA-1：' + $relayCertificate.Thumbprint)
            Write-Output '正在安装当前 Windows 用户的公共根证书；若 Windows 显示安全警告，请核对 CA 指纹后确认…'
            $relayStore.Add($relayCertificate)
            Write-Output 'Windows 根信任已安装，正在保存 Claude Code 的用户环境配置…'
            [Environment]::SetEnvironmentVariable('NODE_EXTRA_CA_CERTS', $relayCombinedPath, 'User')
            $env:NODE_EXTRA_CA_CERTS = $relayCombinedPath
        } catch {
            if (-not $relayAlreadyTrusted) { $relayStore.Remove($relayCertificate) }
            throw
        }
    } finally { $relayStore.Close() }
    Write-Output ('已安装当前用户 CA，SHA-256：' + $relayFingerprint)
    Write-Output ('Claude Code 额外信任：' + $relayCombinedPath)
    Write-Output '请完全退出并重新打开终端、Claude Code 和浏览器。此后日常仅需中转地址和个人令牌。'
} finally { $relayHash.Dispose(); $relayCertificate.Dispose() }
