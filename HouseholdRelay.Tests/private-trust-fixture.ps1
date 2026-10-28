# 专用合成验收：执行正式公共信任包安装，并在结束时恢复测试前的用户信任设置。
param([ValidateSet('Prepare', 'Install', 'Inspect', 'Restore')][string]$Operation, [string]$Directory, [string]$Fingerprint, [string]$Url)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$fixtureReceipt = Join-Path $Directory 'trust-before.json'
$fixtureBundle = Join-Path $Directory 'trust'
if ($Operation -eq 'Prepare') {
    Expand-Archive -LiteralPath (Join-Path $Directory 'client-trust.zip') -DestinationPath $fixtureBundle
    $fixtureCa = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $fixtureBundle 'relay-ca.pem'))
    try {
        $fixtureStore = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'CurrentUser')
        $fixtureStore.Open('ReadOnly')
        try {
            if ($fixtureStore.Certificates.Find('FindByThumbprint', $fixtureCa.Thumbprint, $false).Count -ne 0) { throw '测试根意外已存在，拒绝覆盖。' }
        } finally { $fixtureStore.Close() }
        @{ previous_user_ca = [Environment]::GetEnvironmentVariable('NODE_EXTRA_CA_CERTS', 'User'); thumbprint = $fixtureCa.Thumbprint; fingerprint = $Fingerprint } |
            ConvertTo-Json | Set-Content -LiteralPath $fixtureReceipt -Encoding UTF8
    } finally { $fixtureCa.Dispose() }
    $fixtureRejected = $false
    try { & (Join-Path $fixtureBundle 'Install-RelayTrust.ps1') -ExpectedSha256 ('0' * 64) }
    catch { $fixtureRejected = $true }
    if (-not $fixtureRejected) { throw '错误指纹必须被拒绝。' }
    Write-Output 'Wrong fingerprint rejected before installing trust.'
} elseif ($Operation -eq 'Install') {
    & (Join-Path $fixtureBundle 'Install-RelayTrust.ps1') -ExpectedSha256 $Fingerprint
} elseif ($Operation -eq 'Inspect') {
    $fixtureCaPath = [Environment]::GetEnvironmentVariable('NODE_EXTRA_CA_CERTS', 'User')
    $fixtureResponse = Invoke-WebRequest -UseBasicParsing -Uri ($Url + '/healthz') -TimeoutSec 15
    if ($fixtureResponse.StatusCode -ne 200) { throw 'Windows 系统 TLS 验证失败。' }
    @{ ca_path = $fixtureCaPath; windows_tls_status = $fixtureResponse.StatusCode } | ConvertTo-Json -Compress
} elseif (Test-Path -LiteralPath $fixtureReceipt) {
    $fixtureBefore = Get-Content -LiteralPath $fixtureReceipt -Raw -Encoding UTF8 | ConvertFrom-Json
    $fixtureTrustRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'HouseholdRelay\trust'
    $fixtureInstalled = [IO.Path]::GetFullPath((Join-Path $fixtureTrustRoot $fixtureBefore.fingerprint))
    if ($fixtureBefore.fingerprint -notmatch '^[A-Fa-f0-9]{64}$' -or [IO.Path]::GetDirectoryName($fixtureInstalled) -ne [IO.Path]::GetFullPath($fixtureTrustRoot)) { throw '拒绝清理非本次夹具目录。' }
    $fixtureCurrent = [Environment]::GetEnvironmentVariable('NODE_EXTRA_CA_CERTS', 'User')
    if ($fixtureCurrent -eq (Join-Path $fixtureInstalled 'claude-ca.pem')) {
        [Environment]::SetEnvironmentVariable('NODE_EXTRA_CA_CERTS', $fixtureBefore.previous_user_ca, 'User')
    } elseif ($fixtureCurrent -ne $fixtureBefore.previous_user_ca) { throw '用户信任设置已被其他进程修改，保留现场。' }
    $fixtureStore = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'CurrentUser')
    $fixtureStore.Open('ReadWrite')
    try { foreach ($fixtureCert in $fixtureStore.Certificates.Find('FindByThumbprint', $fixtureBefore.thumbprint, $false)) { $fixtureStore.Remove($fixtureCert) } }
    finally { $fixtureStore.Close() }
    if (Test-Path -LiteralPath $fixtureInstalled) { Remove-Item -LiteralPath $fixtureInstalled -Recurse -Force }
    Write-Output 'Test CA and user environment restored.'
}
