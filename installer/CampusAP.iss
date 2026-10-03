; CampusAP Inno Setup 脚本
; 用法：先跑 发布.ps1（会自动以 /DMyAppVersion=<版本> 调用本脚本），
;       或手动：ISCC.exe /DMyAppVersion=0.3.0 installer\CampusAP.iss
; 版本单源在 Directory.Build.props，不要在这里改版本。

#define MyAppName "CampusAP"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif
#define MyAppPublisher "CampusAP"
#define MyAppExeName "CampusAP.exe"

[Setup]
AppId={{B8F3A2E1-4D5C-7E9A-3F21-1A2B3C4D5E6F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\CampusAP
DefaultGroupName=CampusAP
DisableProgramGroupPage=yes
OutputDir=..\安装包
OutputBaseFilename=CampusAP-Setup-v{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesInstallIn64BitMode=x64compatible

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: checkedonce

[Files]
Source: "..\发布\CampusAP.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\发布\WinDivert.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\发布\WinDivert64.sys"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\CampusAP"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 CampusAP"; Filename: "{uninstallexe}"
Name: "{commondesktop}\CampusAP"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Description: "立即启动 CampusAP"; Filename: "{app}\{#MyAppExeName}"; Flags: nowait postinstall skipifsilent
