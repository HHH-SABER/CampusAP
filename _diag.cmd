@echo off
chcp 65001 >nul
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Requesting administrator rights ^(UAC^)...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
cd /d "%~dp0"
echo [1/4] Stopping running CampusAP...
taskkill /IM CampusAP.exe /F >nul 2>&1
timeout /t 2 /nobreak >nul
echo [2/4] Applying staged v0.3.1...
if exist "_staging\CampusAP.exe" (
  copy /y "_staging\CampusAP.exe" "发布\CampusAP.exe" >nul
  copy /y "_staging\WinDivert.dll" "发布\WinDivert.dll" >nul
  copy /y "_staging\WinDivert64.sys" "发布\WinDivert64.sys" >nul
  rd /s /q _staging
)
echo [3/4] Launching updated app...
start "" "发布\CampusAP.exe"
echo [4/4] Running capture probe (12s)...
tools\CaptureProbe\probebin\CaptureProbe.exe --auto tools\CaptureProbe\probe_result.txt
echo DONE
exit
