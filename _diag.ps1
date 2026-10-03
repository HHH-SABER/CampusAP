# CampusAP 诊断：应用新版本 + 抓包探针（全程写日志，失败可见）
$ErrorActionPreference = 'Continue'
$log = Join-Path $PSScriptRoot '_diag_log.txt'
"=== diag start $(Get-Date -Format 'HH:mm:ss') ===" | Out-File $log -Encoding utf8

function Log($m) { $m | Tee-Object -FilePath $log -Append }

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Log "非管理员，尝试自提权（UAC 弹窗请点是）..."
    Start-Process powershell -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -NoProfile -File `"$PSCommandPath`"" 
    exit
}

try {
    Log "[1/4] 结束运行中的 CampusAP..."
    Get-Process CampusAP -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep 2

    Log "[2/4] 应用 _staging 新版本..."
    $stage = Join-Path $PSScriptRoot '_staging'
    if (Test-Path "$stage\CampusAP.exe") {
        Copy-Item "$stage\CampusAP.exe" "$PSScriptRoot\发布\CampusAP.exe" -Force
        Copy-Item "$stage\WinDivert.dll" "$PSScriptRoot\发布\WinDivert.dll" -Force
        Copy-Item "$stage\WinDivert64.sys" "$PSScriptRoot\发布\WinDivert64.sys" -Force
        Remove-Item $stage -Recurse -Force
        Log "  已替换为: $((Get-Item "$PSScriptRoot\发布\CampusAP.exe").VersionInfo.ProductVersion)"
    } else { Log "  无 _staging（可能已是新版本）" }

    Log "[3/4] 启动新版程序..."
    Start-Process "$PSScriptRoot\发布\CampusAP.exe"

    Log "[4/4] 运行抓包探针 12 秒（手机请保持测速/视频）..."
    $probe = Join-Path $PSScriptRoot 'tools\CaptureProbe\probebin\CaptureProbe.exe'
    & $probe --auto (Join-Path $PSScriptRoot 'tools\CaptureProbe\probe_result.txt') 2>&1 | ForEach-Object { Log "  $_" }
    Log "=== 完成 ==="
} catch {
    Log "异常: $($_ | Out-String)"
}
Log "窗口将在 8 秒后关闭..."
Start-Sleep 8
