#Requires -Version 5.1
<#
.SYNOPSIS
    以低优先级、单节点方式运行 dotnet build / test，避免抢占用户正在使用的音频和读屏。

.DESCRIPTION
    用户在这台机器上一边跑实时音频和读屏办公，一边让智能体跑构建和测试。直接敲
    `dotnet test ZKMedicalSystem.sln` 实测会起 5 个 MSBuild 节点加 2 个 vstest 和 2 个 testhost，
    在 12 个逻辑处理器上把 CPU 吃满，而且 MSBuild 节点的 PriorityClass 是 Normal，
    和前台音频线程平级抢调度，用户那边直接表现为声音卡顿。

    这个脚本做两件事，缺一件都不够：
    - 始终注入 `-m:1`，把 MSBuild 限制成单节点；
    - 用 Start-Process -NoNewWindow -PassThru 起 dotnet，再把 PriorityClass 设成 BelowNormal。
      Windows 的子进程继承父进程的优先级类，所以 dotnet 派生出来的 MSBuild 节点、vstest 和
      testhost 会一并降下来，不必逐个去找它们的进程号。

.NOTES
    脚本参数原样透传给 dotnet，例如 build、test 及其选项；`-m:1` 由脚本注入，不必自己写。

.EXAMPLE
    pwsh -File tools/Invoke-LowPriorityDotnet.ps1 build ZKMedicalSystem.sln

.EXAMPLE
    pwsh -File tools/Invoke-LowPriorityDotnet.ps1 test ZKMedicalSystem.sln --no-build -v:q

.OUTPUTS
    透传 dotnet 的退出码，便于调用方按 0 判定成功。
#>
# 刻意不声明 param：dotnet 的选项和 PowerShell 的公共参数会撞名，实测 `-v:q` 会被绑定成
# `-Verbose` 并当场报错，测试根本没跑起来却显示成失败。改用 $args 全盘接住，一个都不解析。
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($args.Count -eq 0) {
    Write-Host '用法：Invoke-LowPriorityDotnet.ps1 <dotnet 参数...>，例如 test ZKMedicalSystem.sln --no-build -v:q'
    exit 2
}

$dotnetArguments = @($args)

# 已经显式写了并行度就尊重调用方，避免出现 `-m:2 -m:1` 这种自相矛盾的命令行。
$hasNodeLimit = $dotnetArguments | Where-Object { $_ -match '^(-m|--maxcpucount|/m)(:|$)' }
$effectiveArguments = if ($hasNodeLimit) { $dotnetArguments } else { $dotnetArguments + '-m:1' }

# ---- 跑测试时默认留下一份可查的报告（D-046：出错必须有据可查）----
#
# 起因是 2026-09-27 当场付出的代价：用 `-v:q` 跑全套，一条用例红了而日志里**只有一行 [FAIL]、
# 没有任何原因**；重跑一遍带详细输出时**红的换成了另一条**，第一条的现场永远拿不到了。
# D-046 第 3 条禁的正是这个：**不许为了省时间先跑一个不留证据的口径**——
# 红了再重跑是两次成本，而且第二次的现场可能与第一次不同。
#
# **所以留报告这件事不能靠调用方记得加参数**：谁都会在赶时间的时候忘。
# 这里默认挂上 TRX，`-v:q` 丢掉的失败详情原样留在那份文件里。
# **已经自己写了 --logger 的就尊重调用方**，不叠第二个。
$isTest = $dotnetArguments.Count -gt 0 -and $dotnetArguments[0] -eq 'test'
$hasLogger = $dotnetArguments | Where-Object { $_ -match '^(--logger|-l)$' -or $_ -match '^--logger[:=]' }
$trxPath = $null
if ($isTest -and -not $hasLogger) {
    $reportRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\test-reports'
    if (-not (Test-Path $reportRoot)) { $null = New-Item -ItemType Directory -Force -Path $reportRoot }
    # 文件名带时间戳：同一轮里跑好几次时**不许互相覆盖**，否则又回到「第一次的现场没了」。
    $trxName = 'test-{0}.trx' -f (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
    $trxPath = Join-Path $reportRoot $trxName
    $effectiveArguments = $effectiveArguments + @('--logger', "trx;LogFileName=$trxName", '--results-directory', $reportRoot)
}

Write-Host "dotnet $($effectiveArguments -join ' ')  [BelowNormal]"

$process = Start-Process -FilePath 'dotnet' -ArgumentList $effectiveArguments -NoNewWindow -PassThru

# 必须在进程还活着时先摸一次 Handle：.NET 的 Process 要靠缓存下来的句柄才读得到退出码，
# 不摸的话 WaitForExit 之后 ExitCode 恒为 0——实测跑出 1 条失败的测试仍然报成功，
# 这种假成功比慢危险得多。
$null = $process.Handle

try {
    # 优先级必须在进程起来之后设：句柄还没建好时赋值会抛异常。进程如果跑得极快，
    # 这里可能已经退出，那时降不降优先级都没有意义，所以吞掉这一种失败继续等退出码。
    $process.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal
}
catch [System.InvalidOperationException] {
    Write-Host '进程已退出，跳过优先级设置。'
}

$process.WaitForExit()

# ---- 失败时当场把原因摆出来，不必再重跑一遍（D-046 第 4 条：先看报告，再动手查）----
#
# **只在失败时打，而且只打失败的那几条**：用户开着读屏，全绿时多输出一屏是纯打扰。
# 打的是「用例名 ＋ 错误消息」，正是重跑一遍想拿到的那两样。
if ($trxPath -and (Test-Path $trxPath)) {
    Write-Host "测试报告：$trxPath"
    if ($process.ExitCode -ne 0) {
        try {
            [xml]$trx = Get-Content -LiteralPath $trxPath -Raw -Encoding UTF8
            # **只认 Failed**：跳过的那一批（本工程默认不跑真窗口用例，一次就有 62 条）
            # outcome 是 NotExecuted，按「非 Passed」筛会把它们全打出来，
            # 真正那一条失败反而被淹掉——第一次用这个脚本就撞到了。
            $failed = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' })
            foreach ($one in $failed) {
                Write-Host "失败：$($one.testName)"
                if ($one.Output -and $one.Output.ErrorInfo -and $one.Output.ErrorInfo.Message) {
                    Write-Host "  原因：$($one.Output.ErrorInfo.Message)"
                }
            }
        }
        # 报告读不出来不能把「测试失败」这个结论盖掉，也不能假装成功：
        # 如实说一句，退出码照原样传出去。
        catch {
            Write-Host "测试报告解析不了，请直接打开：$trxPath"
        }
    }
}

exit $process.ExitCode
