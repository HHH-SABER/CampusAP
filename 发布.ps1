# CampusAP 发布脚本：单文件发布 + WinDivert 原生文件就位 + Inno Setup 安装包（检测到则自动打包）
# 用法：powershell -File 发布.ps1
# 版本单源在 Directory.Build.props：改版本只改那里，本脚本把版本传给安装包（/DMyAppVersion）。
# 程序侧会监视 _staging\version.txt 自动弹窗应用新版本；发布目录被锁时安装包直接从 _staging 打，永不漏打。
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# ---- 版本（单源读取）----
$propsXml = [xml](Get-Content "$root\Directory.Build.props" -Raw)
$version = @($propsXml.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { throw "无法从 Directory.Build.props 读取版本号" }
Write-Output "版本：v$version"

# ---- Inno Setup 探测（提前，锁定分支也要打安装包）----
$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

function Build-Installer([string]$publishDir) {
    if (-not $iscc) {
        Write-Output "`n未检测到 Inno Setup 6（ISCC.exe），已跳过安装包打包。"
        return
    }
    & $iscc "/DMyAppVersion=$version" "/DPublishDir=$publishDir" "$root\installer\CampusAP.iss"
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup 打包失败" }
    Write-Output "`n安装包已生成：安装包\CampusAP-Setup-v$version.exe"
}

# ---- 编译 + 单文件发布（先进暂存目录，再尝试落入发布目录）----
$stage = "$root\_staging"
& dotnet publish "$root\src\CampusAP.App" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

Remove-Item "$stage\*.pdb" -Force -ErrorAction SilentlyContinue
Set-Content -Path (Join-Path $stage 'version.txt') -Value $version -Encoding Ascii

# WinDivert 内核驱动与 DLL 必须与主程序同目录（单文件打包不会带上 .sys）
$native = Join-Path $env:USERPROFILE '.nuget\packages\windivertsharp\1.4.3.2\build\x64'
Copy-Item "$native\WinDivert.dll"   "$stage\" -Force
Copy-Item "$native\WinDivert64.sys" "$stage\" -Force

# 尝试把三件套落进 发布\（运行中的旧版本会锁文件）
$locked = $false
foreach ($f in 'CampusAP.exe', 'WinDivert.dll', 'WinDivert64.sys') {
    try { Copy-Item "$stage\$f" "$root\发布\$f" -Force -ErrorAction Stop }
    catch { $locked = $true; break }
}

if ($locked) {
    # 生成交接脚本：apply_update.ps1（等待进程退出+逐文件校验）+ 两行 bat 启动器（自提权）
    $applyPs1 = @'
# CampusAP staged-update applier (ASCII only; self-elevates; waits for exit; verifies swap).
$ErrorActionPreference = 'Stop'
function Is-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
if (-not (Is-Admin)) {
    Start-Process powershell -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -NoProfile -File `"$PSCommandPath`""
    exit
}
$root = Split-Path -Parent $PSCommandPath
$stage = Join-Path $root '_staging'
if (-not (Test-Path (Join-Path $stage 'CampusAP.exe'))) {
    Write-Host 'No staged version found (_staging\CampusAP.exe). Nothing to apply.'
    Start-Sleep 5
    exit 1
}

Write-Host '[1/4] Stopping running CampusAP...'
Get-Process CampusAP -ErrorAction SilentlyContinue | Stop-Process -Force
$deadline = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $deadline -and (Get-Process CampusAP -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 300 }

Write-Host '[2/4] Swapping files (staged -> publish)...'
$pub = (Get-ChildItem $root -Directory | Where-Object {
    $_.Name -ne '_staging' -and (Test-Path (Join-Path $_.FullName 'CampusAP.exe'))
} | Select-Object -First 1).FullName
if (-not $pub) { Write-Host 'Publish dir not found.'; Start-Sleep 5; exit 1 }
$failed = $false
foreach ($f in 'CampusAP.exe', 'WinDivert.dll', 'WinDivert64.sys') {
    try {
        Copy-Item (Join-Path $stage $f) (Join-Path $pub $f) -Force -ErrorAction Stop
    } catch {
        Write-Host "  FAILED: $f : $($_.Exception.Message)"
        $failed = $true
    }
}
if ($failed) {
    Write-Host 'Swap FAILED - staged files kept in _staging, rerun this script after closing CampusAP.'
    Start-Sleep 8
    exit 1
}

Write-Host '[3/4] Cleaning staging...'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

Write-Host '[4/4] Launching updated version...'
Start-Process (Join-Path $pub 'CampusAP.exe')
Write-Host "Done. Applied version: $((Get-Item (Join-Path $pub 'CampusAP.exe')).VersionInfo.ProductVersion)"
Start-Sleep 4
'@
    Set-Content -Path "$root\apply_update.ps1" -Value $applyPs1 -Encoding UTF8
    Set-Content -Path "$root\apply_update.bat" -Value "powershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0apply_update.ps1`"" -Encoding Ascii
    Write-Output "`n发布目录被运行中的 CampusAP 锁定：新版本已备好于 _staging，"
    Write-Output "程序会自动弹窗提示应用（或双击 apply_update.bat）。安装包改从 _staging 打。"
    Build-Installer $stage
    exit 0
}

Write-Output "`n发布完成："
Get-ChildItem "$root\发布" | Format-Table Name, @{L='Size(MB)';E={'{0:N1}' -f ($_.Length/1MB)}} -AutoSize
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Build-Installer "$root\发布"
