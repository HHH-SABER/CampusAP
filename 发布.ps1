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

# ---- 编译 + 单文件发布 ----
& dotnet publish "$root\src\CampusAP.App" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o "$root\发布"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

Remove-Item "$root\发布\*.pdb" -Force -ErrorAction SilentlyContinue

# WinDivert 内核驱动与 DLL 必须与主程序同目录（单文件打包不会带上 .sys）
$native = Join-Path $env:USERPROFILE '.nuget\packages\windivertsharp\1.4.3.2\build\x64'
Copy-Item "$native\WinDivert.dll"   "$root\发布\" -Force
Copy-Item "$native\WinDivert64.sys" "$root\发布\" -Force

Write-Output "`n发布完成："
Get-ChildItem "$root\发布" | Format-Table Name, @{L='Size(MB)';E={'{0:N1}' -f ($_.Length/1MB)}} -AutoSize

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
