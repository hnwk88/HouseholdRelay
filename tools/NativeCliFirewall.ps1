param(
    [Parameter(Mandatory = $true)][ValidateSet('Install', 'Status', 'Remove', 'Guard')][string]$Action,
    [Parameter(Mandatory = $true)][string]$OwnerGuid,
    [Parameter(Mandatory = $true)][string]$Executable
)

$ErrorActionPreference = 'Stop'
$owner = [Guid]::ParseExact($OwnerGuid, 'N').ToString('N')
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$isolationRoot = [IO.Path]::Combine($temporaryRoot, 'HouseholdRelay-native-isolation-' + $owner)
$expectedExecutable = [IO.Path]::Combine($isolationRoot, 'claude.exe')
$program = [IO.Path]::GetFullPath($Executable)
if (-not [string]::Equals($program, $expectedExecutable, [StringComparison]::OrdinalIgnoreCase)) {
    throw '程序必须是本次 owner 对应临时目录中的专用 claude.exe 副本。'
}

# 沿目标到临时根检查现存链接；只允许专用测试副本，不能借目录链接指向个人安装。
$check = $program
while ($check -and $check.Length -ge $temporaryRoot.Length) {
    if (Test-Path -LiteralPath $check) {
        if ((Get-Item -LiteralPath $check -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '隔离路径含重解析点，拒绝修改规则。' }
    }
    if ([string]::Equals($check, $temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
    $check = [IO.Path]::GetDirectoryName($check)
}
if (-not (Test-Path -LiteralPath $isolationRoot -PathType Container)) { throw '本次隔离目录不存在。' }

$group = 'HouseholdRelay native isolation ' + $owner
$receiptPath = [IO.Path]::Combine($isolationRoot, 'firewall-receipt.json')
$families = @(
    @{ Suffix = 'ipv4'; Addresses = @('0.0.0.0-126.255.255.255', '128.0.0.0-255.255.255.255') },
    @{ Suffix = 'ipv6'; Addresses = @('::/128', '::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff') }
)
$names = @($families | ForEach-Object { 'HouseholdRelay-native-' + $owner + '-' + $_.Suffix })
$profiles = @()
if ($Action -ne 'Remove') { $profiles = @(Get-NetConnectionProfile | ForEach-Object {
    $profileName = switch ($_.NetworkCategory.ToString()) { 'DomainAuthenticated' { 'Domain' }; 'Private' { 'Private' }; 'Public' { 'Public' }; default { throw '活动网络类别不明。' } }
    $profile = Get-NetFirewallProfile -PolicyStore ActiveStore -Name $profileName
    [pscustomobject]@{ Name = $profileName; Enabled = [bool]($profile.Enabled -eq 'True') }
}) }

function Read-OwnedRules {
    $found = @()
    foreach ($name in $names) {
        $rule = Get-NetFirewallRule -PolicyStore PersistentStore -Name $name -ErrorAction SilentlyContinue
        if (-not $rule) { continue }
        $application = $rule | Get-NetFirewallApplicationFilter
        if ($rule.Group -ne $group -or -not [string]::Equals($application.Program, $program, [StringComparison]::OrdinalIgnoreCase)) {
            throw '同名规则不属于本次专用副本，拒绝覆盖或移除。'
        }
        $found += $rule
    }
    return $found
}

function Write-Receipt([bool]$ready, [string]$result, [string]$knownHash = $null) {
    $rules = @(Read-OwnedRules)
    $data = [ordered]@{
        schema = 1; owner = $owner; executable = $program; ready = $ready; result = $result
        updated_utc = [DateTime]::UtcNow.ToString('o'); active_profiles = $profiles
        executable_sha256 = if ($knownHash) { $knownHash } elseif (Test-Path -LiteralPath $program -PathType Leaf) { (Get-FileHash -LiteralPath $program -Algorithm SHA256).Hash } else { $null }
        rules = @($rules | ForEach-Object {
            [ordered]@{ name = $_.Name; enabled = $_.Enabled.ToString(); direction = $_.Direction.ToString(); action = $_.Action.ToString()
                program = ($_ | Get-NetFirewallApplicationFilter).Program; remote_addresses = @(( $_ | Get-NetFirewallAddressFilter).RemoteAddress) }
        })
        scope = '只限制本次测试复制程序的非回环出站；不修改防火墙配置文件开关，不涉及个人 Claude 或正式服务。'
    }
    [IO.File]::WriteAllText($receiptPath, ($data | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($true))
    Write-Output ($data | ConvertTo-Json -Depth 8)
}

function Normalize-Address([string]$address) {
    # 防火墙可能把单地址 /128 或 IPv6 大写写成规范形式，按地址值核对，不放宽范围。
    $parts = $address.Replace('/128', '').Replace('/32', '').Split('-')
    return (($parts | ForEach-Object { ([Net.IPAddress]::Parse($_)).ToString() }) -join '-')
}

function Stop-GuardProcesses {
    $cleanup = [ordered]@{ owner = $owner; executable = $program; result = 'cleanup-started'; matched = 0; terminated = @(); confirmed_zero = $false; launch_lock = $false }
    $cleanupPath = [IO.Path]::Combine($isolationRoot, 'cleanup-processes.json')
    $launchLock = $null; $knownHash = $null
    try {
        # 首先撤销测试启动器的 ready 回执，阻止后续调用继续启动这份复制程序。
        Write-Receipt $false 'cleanup-started'
        $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'claude.exe'" | Where-Object {
            [string]::Equals($_.ExecutablePath, $program, [StringComparison]::OrdinalIgnoreCase)
        })
        $cleanup.matched = $processes.Count
        if ($processes.Count -gt 16) { throw '本次复制程序进程数量超出清理边界。' }
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        foreach ($observed in $processes) {
            $nativeProcess = $null
            try {
                # PID 可能重用，动手前再次核对当前进程完整路径；绝不按 claude 名称批量结束。
                try { $nativeProcess = [Diagnostics.Process]::GetProcessById([int]$observed.ProcessId) }
                catch [ArgumentException] { continue }
                if ($nativeProcess.HasExited) { continue }
                if (-not [string]::Equals($nativeProcess.MainModule.FileName, $program, [StringComparison]::OrdinalIgnoreCase)) { throw '进程身份在核对期间改变。' }
                $nativeProcess.Kill()
                $remaining = [Math]::Max(0, 20000 - [int]$deadline.ElapsedMilliseconds)
                if (-not $nativeProcess.WaitForExit($remaining)) { throw '无法在本次清理预算内确认测试进程退出。' }
                $cleanup.terminated += [int]$observed.ProcessId
            }
            finally { if ($nativeProcess) { $nativeProcess.Dispose() } }
        }
        $remainingProcesses = @(Get-CimInstance Win32_Process -Filter "Name = 'claude.exe'" | Where-Object {
            [string]::Equals($_.ExecutablePath, $program, [StringComparison]::OrdinalIgnoreCase)
        })
        if ($remainingProcesses.Count -ne 0) { throw '仍有本次测试复制程序在运行。' }
        $knownHash = (Get-FileHash -LiteralPath $program -Algorithm SHA256).Hash
        # 无共享读句柄阻止规则移除期间重新打开该 exe 创建进程；拿不到锁就保留网络保护。
        $launchLock = [IO.File]::Open($program, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        $cleanup.launch_lock = $true
        $lastCheck = @(Get-CimInstance Win32_Process -Filter "Name = 'claude.exe'" | Where-Object {
            [string]::Equals($_.ExecutablePath, $program, [StringComparison]::OrdinalIgnoreCase)
        })
        if ($lastCheck.Count -ne 0) { throw '关闭启动窗口前仍出现本次测试进程。' }
        $cleanup.confirmed_zero = $true
        # 只有确定无在途复制程序且已阻止重新启动时才撤销本 owner 规则。
        foreach ($rule in @(Read-OwnedRules)) { Remove-NetFirewallRule -InputObject $rule }
        $cleanup.result = 'removed'
        Write-Receipt $false 'removed' $knownHash
    }
    catch {
        $cleanup.result = 'cleanup-incomplete'
        # 退出证据不足时保留现有规则，回执明确未清理完成，不假装已解除隔离。
        Write-Receipt $false 'cleanup-incomplete' $knownHash
        throw '未能确认本次测试复制程序全部退出或规则全部清除；保留网络保护，请检查本 owner 的清理回执。'
    }
    finally {
        if ($launchLock) { $launchLock.Dispose() }
        [IO.File]::WriteAllText($cleanupPath, ($cleanup | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($true))
    }
}

if ($Action -eq 'Guard') {
    $stopPath = [IO.Path]::Combine($isolationRoot, 'stop')
    if (Test-Path -LiteralPath $stopPath) { throw '本 owner 的停止标记已经存在，请为新一轮使用新的 owner。' }
    $guardStarted = [DateTime]::Now
    try {
        # 用结构化参数复用同一脚本的安装和核验；不拼接可执行命令，不替用户再次提权。
        & $PSCommandPath -Action Install -OwnerGuid $owner -Executable $program
        $guardClock = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath $stopPath) -and $guardClock.Elapsed -lt [TimeSpan]::FromMinutes(15)) {
            Start-Sleep -Seconds 1
        }
    }
    finally {
        try {
            # 只读既有 WFP 拒绝事件，不开启全局审核；没有事件不能被写成网络阻止已获实测证明。
            $denials = @(); $audit = 'no-matching-event-or-audit-unavailable'
            try {
                $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Security'; Id = 5157; StartTime = $guardStarted } -ErrorAction Stop)
                foreach ($event in $events) {
                    [xml]$xml = $event.ToXml(); $fields = @{}
                    foreach ($field in $xml.Event.EventData.Data) { $fields[$field.Name] = $field.'#text' }
                    if ($fields.Application -and $fields.Application.EndsWith(('HouseholdRelay-native-isolation-' + $owner + '\claude.exe'), [StringComparison]::OrdinalIgnoreCase)) {
                        $denials += [ordered]@{ event_id = 5157; at = $event.TimeCreated.ToUniversalTime().ToString('o'); process_id = $fields.ProcessID
                            application = $fields.Application; destination = $fields.DestAddress; destination_port = $fields.DestPort; filter_id = $fields.FilterRTID }
                    }
                }
                if ($denials.Count -gt 0) { $audit = 'matching-wfp-denial-recorded' }
            }
            catch { $audit = 'no-matching-event-or-audit-unavailable' }
            $denialEvidence = [ordered]@{ owner = $owner; audit = $audit; events = $denials; audit_policy_changed = $false }
            [IO.File]::WriteAllText([IO.Path]::Combine($isolationRoot, 'network-denials.json'), ($denialEvidence | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($true))
        }
        finally {
            # 正常结束、失败或十五分钟到期都先确认测试复制程序退出，再移除精确程序规则。
            Stop-GuardProcesses
        }
    }
    exit 0
}

if ($Action -eq 'Remove') {
    # 只移除本 owner 且应用路径完全匹配的规则；不会批量操作其他工程或个人程序。
    foreach ($rule in @(Read-OwnedRules)) { Remove-NetFirewallRule -InputObject $rule }
    Write-Receipt $false 'removed'
    exit 0
}

if ($profiles.Count -eq 0 -or @($profiles | Where-Object { -not $_.Enabled }).Count -ne 0) {
    Write-Receipt $false 'active-profile-not-enabled'
    throw '当前活动网络没有全部启用防火墙；本脚本不会更改全局开关，未允许启动真 CLI。'
}
if (-not (Test-Path -LiteralPath $program -PathType Leaf)) { throw '本次测试复制程序不存在。' }

if ($Action -eq 'Install') {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '安装规则需要本次临时提升；脚本不会自行触发 UAC。' }
    $created = @()
    try {
        $existing = @(Read-OwnedRules)
        foreach ($family in $families) {
            $name = 'HouseholdRelay-native-' + $owner + '-' + $family.Suffix
            if (@($existing | Where-Object Name -EQ $name).Count -ne 0) { continue }
            New-NetFirewallRule -PolicyStore PersistentStore -Name $name -DisplayName $name -Group $group -Direction Outbound -Action Block -Enabled True -Profile Any -Program $program -RemoteAddress $family.Addresses | Out-Null
            $created += $name
        }
    }
    catch {
        # 安装中途失败只撤回本次已创建的规则；既有规则及全局策略完全不变。
        foreach ($name in $created) { Remove-NetFirewallRule -PolicyStore PersistentStore -Name $name }
        Write-Receipt $false 'install-failed'
        throw
    }
}

$owned = @(Read-OwnedRules)
if ($owned.Count -ne 2 -or @($owned | Where-Object { $_.Enabled -ne 'True' -or $_.Direction -ne 'Outbound' -or $_.Action -ne 'Block' }).Count -ne 0) {
    Write-Receipt $false 'rule-verification-failed'
    throw '规则数量或方向、动作、开关不符合预期，未允许启动真 CLI。'
}
# 本次专用规则还须出现在活动策略中；不把仅保存却未生效的配置当作隔离成功。
foreach ($name in $names) {
    $active = Get-NetFirewallRule -PolicyStore ActiveStore -Name $name -ErrorAction SilentlyContinue
    if (-not $active -or $active.Enabled -ne 'True' -or $active.Direction -ne 'Outbound' -or $active.Action -ne 'Block' -or $active.Profile -ne 'Any') {
        Write-Receipt $false 'rule-not-active'
        throw '本次规则未进入活动策略，未允许启动真 CLI。'
    }
    $family = $families | Where-Object { $name.EndsWith($_.Suffix) }
    $expected = @($family.Addresses | ForEach-Object { Normalize-Address $_ } | Sort-Object)
    $actual = @(($active | Get-NetFirewallAddressFilter).RemoteAddress | ForEach-Object { Normalize-Address $_ } | Sort-Object)
    if (($expected -join ',') -ne ($actual -join ',')) {
        Write-Receipt $false 'remote-scope-mismatch'
        throw '本次活动规则的远端范围不符合预期，未允许启动真 CLI。'
    }
}
Write-Receipt $true 'ready'
