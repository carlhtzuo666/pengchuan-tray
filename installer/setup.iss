; ============================================================
;  PengchuanTray installer script (Inno Setup 6)
;
;  Per-user install (PrivilegesRequired=lowest): installs into
;  %LOCALAPPDATA%\PengchuanTray, no administrator rights needed.
;
;  VERSION: never hardcode it here. The build script
;           (..\scripts\build-installer.ps1) reads <Version> from
;           PengchuanTray.csproj — the single source of truth — and
;           passes /DMyAppVersion=x.y.z to ISCC. The default below
;           only exists so opening this file in the IDE does not fail.
;
;  Build:
;    powershell -NoProfile -ExecutionPolicy Bypass -File ..\scripts\build-installer.ps1
; ============================================================

#ifndef MyAppVersion
  #error "MyAppVersion must be passed by build-installer.ps1 (/DMyAppVersion=x.y.z). Do not hardcode a version in this file."
#endif

#define MyAppName "贴传助手"
#define MyAppPublisher "ailinkfree"
#define MyAppExeName "PengchuanTray.exe"
#ifndef PayloadDir
  #define PayloadDir "D:\PengchuanTray\publish"
#endif
#ifndef ExtraDir
  #define ExtraDir "D:\PengchuanTray\dist-package"
#endif

[Setup]
AppId={{9E2C4B7A-3F51-4D8E-9A6B-5C1D2E7F8A03}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\PengchuanTray
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=D:\tools\harness-personal\pengchuan-tray\installer\output
OutputBaseFilename=PengchuanTray-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

[Languages]
Name: "cn"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "{#PayloadDir}\PengchuanTray.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ExtraDir}\install-cert.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ExtraDir}\test-selfsigned.cer"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\安装测试证书（首次使用前运行）"; Filename: "{app}\install-cert.bat"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\install-cert.bat"; Description: "安装测试签名证书（推荐，消除“未知发布者”提示）"; Flags: shellexec nowait postinstall skipifsilent
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "schtasks.exe"; Parameters: "/delete /tn ""贴传助手"" /f"; Flags: runhidden; RunOnceId: "DelTask"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\startup.log"
Type: filesandordirs; Name: "{app}\tray.log"