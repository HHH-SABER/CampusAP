@echo off
chcp 65001 >nul
rem WinDivert diagnostic probe: self-elevate (UAC), publish, copy native files, run.
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Requesting administrator rights ^(UAC^)...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
cd /d "%~dp0"
echo Publishing CaptureProbe...
dotnet publish CaptureProbe.csproj -c Release -o probebin
if errorlevel 1 (
  echo Build FAILED. Is .NET 8 SDK installed?
  pause
  exit /b 1
)
copy /y "%USERPROFILE%\.nuget\packages\windivertsharp\1.4.3.2\build\x64\WinDivert.dll" probebin\ >nul
copy /y "%USERPROFILE%\.nuget\packages\windivertsharp\1.4.3.2\build\x64\WinDivert64.sys" probebin\ >nul
probebin\CaptureProbe.exe
pause
