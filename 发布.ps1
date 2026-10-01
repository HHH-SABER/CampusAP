# CampusAP 发布脚本：单文件发布 + WinDivert 原生文件就位
# 用法：powershell -File 发布.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

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
