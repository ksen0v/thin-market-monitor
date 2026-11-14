#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef AppExe
  #error AppExe is required
#endif

[Setup]
AppId=ThinMarketMonitor
AppName=Thin Market Monitor
AppVersion={#AppVersion}
AppPublisher=Thin Market Monitor
AppPublisherURL=https://github.com/ksen0v/thin-market-monitor
AppUpdatesURL=https://github.com/ksen0v/thin-market-monitor/releases
DefaultDirName={localappdata}\Programs\Thin Market Monitor
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
OutputBaseFilename=ThinMarketMonitor-Setup-win-x64
SetupIconFile=..\src\ThinMarketMonitor\Assets\terminal.ico
UninstallDisplayName=Thin Market Monitor
UninstallDisplayIcon={app}\ThinMarketMonitor.exe
VersionInfoVersion={#AppVersion}
WizardStyle=modern
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
english.StartApp=Launch Thin Market Monitor
russian.StartApp=Запустить Thin Market Monitor
english.NewerVersion=A newer version of Thin Market Monitor is already installed.
russian.NewerVersion=Уже установлена более новая версия Thin Market Monitor.
english.KeepLocation=Update Thin Market Monitor in the existing installation folder: %1
russian.KeepLocation=Обновите Thin Market Monitor в текущей папке установки: %1

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#AppExe}"; DestDir: "{app}"; DestName: "ThinMarketMonitor.exe"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Thin Market Monitor"; Filename: "{app}\ThinMarketMonitor.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Thin Market Monitor"; Filename: "{app}\ThinMarketMonitor.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\ThinMarketMonitor.exe"; Description: "{cm:StartApp}"; Flags: nowait postinstall skipifsilent

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\ThinMarketMonitor_is1';

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Location, Installed: String;
  ExistingVersion, SetupVersion: Int64;
begin
  Result := '';
  if RegQueryStringValue(HKCU64, UninstallKey, 'DisplayVersion', Installed) and
    StrToVersion(Installed, ExistingVersion) and StrToVersion('{#AppVersion}', SetupVersion) and
    (ComparePackedVersion(ExistingVersion, SetupVersion) > 0) then
  begin
    Result := CustomMessage('NewerVersion');
    Exit;
  end;
  if RegQueryStringValue(HKCU64, UninstallKey, 'InstallLocation', Location) and (Location <> '') and
    (CompareText(RemoveBackslashUnlessRoot(Location), RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) <> 0) then
    Result := FmtMessage(CustomMessage('KeepLocation'), [Location]);
end;
