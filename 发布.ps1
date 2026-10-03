# CampusAP 发布脚本：单文件发布 + WinDivert 原生文件就位 + Inno Setup 安装包（检测到则自动打包）
# 用法：powershell -File 发布.ps1
# 版本单源在 Directory.Build.props：改版本只改那里，本脚本把版本传给安装包（/DMyAppVersion）。
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# ---- 版本（单源读取）----
$propsXml = [xml](Get-Content "$root\Directory.Build.props" -Raw)
$version = @($propsXml.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { throw "无法从 Directory.Build.props 读取版本号" }
Write-Output "版本：v$version"

# ---- 编译 + 单文件发布（先进暂存目录，再尝试落入发布目录；被运行中的程序锁定时给出一键替换脚本）----
$stage = "$root\_staging"
& dotnet publish "$root\src\CampusAP.App" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

Remove-Item "$stage\*.pdb" -Force -ErrorAction SilentlyContinue
Set-Content -Path "$stage\version.txt" -Value $version -Encoding Ascii

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
    # 生成一键替换脚本：自提权 → 结束运行中的 CampusAP → 交换文件 → 重启
    $applyBat = @'
@echo off
chcp 65001 >nul
net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
cd /d "%~dp0"
echo Stopping running CampusAP...
taskkill /IM CampusAP.exe /F >nul 2>&1
timeout /t 2 /nobreak >nul
copy /y "_staging\CampusAP.exe" "发布\CampusAP.exe" >nul
copy /y "_staging\WinDivert.dll" "发布\WinDivert.dll" >nul
copy /y "_staging\WinDivert64.sys" "发布\WinDivert64.sys" >nul
if errorlevel 1 ( echo Swap FAILED. & pause & exit /b 1 )
rd /s /q _staging
echo Updated. Launching new version...
start "" "发布\CampusAP.exe"
'@
    Set-Content -Path "$root\应用新版本.bat" -Value $applyBat -Encoding UTF8
    Write-Output "`n发布目录被运行中的 CampusAP 锁定：新版本已备好于 _staging，"
    Write-Output "双击 应用新版本.bat 一键完成替换并重启（或关闭程序后重跑本脚本）。"
    exit 0
}

Write-Output "`n发布完成："
Get-ChildItem "$root\发布" | Format-Table Name, @{L='Size(MB)';E={'{0:N1}' -f ($_.Length/1MB)}} -AutoSize
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

# ---- 安装包（检测到 Inno Setup 6 就顺带打包；产物走 GitHub Releases，不入库）----
$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if ($iscc) {
    & $iscc "/DMyAppVersion=$version" "$root\installer\CampusAP.iss"
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup 打包失败" }
    Write-Output "`n安装包已生成：安装包\CampusAP-Setup-v$version.exe"
} else {
    Write-Output "`n未检测到 Inno Setup 6（ISCC.exe），已跳过安装包打包。"
}
