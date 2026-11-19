<#
.SYNOPSIS
  用旧版正式程序的源码生成真实旧库夹具（不删表删列、不改版本号伪造）。
.DESCRIPTION
  1. 在临时目录为 -Commit 建一个独立 git 工作树（只读用，结束即删）；
  2. 把同目录的 Program.cs 复制到临时驱动工程，工程文件引用那个工作树里的 src/HouseholdRelay，版本 3 加条件编译符号 FIXTURE_V3、版本 9 加 FIXTURE_V9；
  3. 低优先级、单并行编译并运行驱动，只调旧版公开服务在空数据目录里写数据；
  4. 把数据目录的 .relay-instance 和 .state\relay.db 复制到 tests\Tests\Fixtures\LegacyDatabases\<Name>\，并写 SOURCE.txt 记下来源提交与库版本；
     版本 9 另带官方账户目录 .state\official-accounts、明文假值 plain.json 和导出文件 upstreams.export。
  只能在 PowerShell 5.1 上用 .NET Framework 的 API；不连网。
.EXAMPLE
  powershell -File tools\legacy-fixtures\New-LegacyFixture.ps1 -Commit 39c6785 -Name v3 -V3
  powershell -File tools\legacy-fixtures\New-LegacyFixture.ps1 -Commit 2dd823c -Name v8
  powershell -File tools\legacy-fixtures\New-LegacyFixture.ps1 -Commit bb20b32 -Name v9 -V9
#>
param(
    [Parameter(Mandatory = $true)][string]$Commit,
    [Parameter(Mandatory = $true)][string]$Name,
    [switch]$V3,
    [switch]$V9
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ('hr-legacy-' + $Name + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$tree = Join-Path $work 'tree'; $driver = Join-Path $work 'driver'; $home2 = Join-Path $work 'home'
$target = Join-Path $repo ('tests\Tests\Fixtures\LegacyDatabases\' + $Name)

function Invoke-Low([string]$file, [string[]]$arguments, [string]$log) {
    # 用户开着实时音频和读屏：子进程一律降到低于正常优先级，输出进日志文件。
    $process = Start-Process $file -ArgumentList $arguments -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError ($log + '.err')
    # 先取一次句柄：不取的话进程结束后 ExitCode 读成空。
    $null = $process.Handle
    try { $process.PriorityClass = 'BelowNormal' } catch { }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw ("命令失败（退出码 " + $process.ExitCode + "）：" + $file + "，日志 " + $log) }
}

New-Item -ItemType Directory -Force $driver | Out-Null
$done = $false
try {
    & git -C $repo worktree add --detach $tree $Commit | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "建工作树失败：$Commit" }
    $full = (& git -C $tree rev-parse HEAD).Trim()
    Copy-Item (Join-Path $PSScriptRoot 'Program.cs') $driver
    $constants = if ($V3) { '<DefineConstants>$(DefineConstants);FIXTURE_V3</DefineConstants>' } elseif ($V9) { '<DefineConstants>$(DefineConstants);FIXTURE_V9</DefineConstants>' } else { '' }
    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>LegacyFixtureDriver</AssemblyName>
    $constants
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="$tree\src\HouseholdRelay\HouseholdRelay.csproj" />
  </ItemGroup>
</Project>
"@
    [IO.File]::WriteAllText((Join-Path $driver 'LegacyFixtureDriver.csproj'), $project, (New-Object Text.UTF8Encoding $true))
    # 旧提交用的 SQLite 包后来有了漏洞公告，旧工程把公告警告当错误编不过；夹具要的就是那一版原样的程序，只在本机离线跑一次，所以只关审计，不升包。
    Invoke-Low 'dotnet' @('build', (Join-Path $driver 'LegacyFixtureDriver.csproj'), '-c', 'Release', '-m:1', '-nologo', '-p:NuGetAudit=false', '-o', (Join-Path $driver 'out')) (Join-Path $work 'build.log')
    Invoke-Low (Join-Path $driver 'out\LegacyFixtureDriver.exe') @($home2) (Join-Path $work 'run.log')
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $home2 '.relay-instance') $target
    Copy-Item (Join-Path $home2 '.state\relay.db') $target
    foreach ($side in '-wal', '-shm') { if (Test-Path (Join-Path $home2 ('.state\relay.db' + $side))) { throw "驱动结束后仍有 relay.db$side，库没有关干净。" } }
    if ($V9) {
        Copy-Item (Join-Path $home2 'plain.json') $target
        Copy-Item (Join-Path $home2 'upstreams.export') $target
        Copy-Item -Recurse (Join-Path $home2 '.state\official-accounts') (Join-Path $target 'official-accounts')
    }
    $switch = if ($V3) { ' -V3' } elseif ($V9) { ' -V9' } else { '' }
    $source = "来源提交：$full`r`n生成方式：tools\legacy-fixtures\New-LegacyFixture.ps1 -Commit $Commit -Name $Name" + $switch + "`r`n生成时间（UTC）：" + [DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss') + "`r`n内容：那一版的程序经它自己的公开服务写入，密钥都是假值，凭据用生成那台电脑当时的 Windows 用户加密（升级不解密）。`r`n"
    [IO.File]::WriteAllText((Join-Path $target 'SOURCE.txt'), $source, (New-Object Text.UTF8Encoding $true))
    Write-Output ("已生成夹具：" + $target)
    $done = $true
}
finally {
    & git -C $repo worktree remove --force $tree 2>$null | Out-Null
    # 失败时留下临时目录里的编译和运行日志供排查，成功才删。
    if ($done) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue } else { Write-Output ("失败，日志留在：" + $work) }
}
