; Установщик ZeTwitchMiner. Собирается из build.ps1, версия приходит через /DAppVersion
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppName "ZeTwitchMiner"
#define AppExe "ZeTwitchMiner.exe"

[Setup]
AppId={{6C1C0B8E-2E7A-4F43-9B7E-5A8E3D2F7A11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=zuckerigprod
AppPublisherURL=https://github.com/zuckerigprod
AppSupportURL=https://github.com/zuckerigprod/ZeTwitchMiner
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=ZeTwitchMiner-{#AppVersion}-setup
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dynamic
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=Local\ZeTwitchMiner.Instance
CloseApplications=yes

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\*.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Обновление из программы идёт в тихом режиме, после него запускаем её снова
Filename: "{app}\{#AppExe}"; Flags: nowait skipifnotsilent

[UninstallRun]
; Убираем автозапуск, если пользователь его включал
Filename: "reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v ZeTwitchMiner /f"; Flags: runhidden; RunOnceId: "RemoveAutostart"
