#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#ifndef PublishRoot
  #error PublishRoot must point to the self-contained publish directory.
#endif

#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
AppId={{D059DD5D-ECC5-4BA9-B95D-9B8361F813C5}
AppName=Windows Developer Environment Manager
AppVersion={#MyAppVersion}
AppVerName=WDEM {#MyAppVersion}
AppPublisher=WDEM Contributors
AppPublisherURL=https://github.com/JasonLiCSHI/WDEM
AppSupportURL=https://github.com/JasonLiCSHI/WDEM/issues
AppUpdatesURL=https://github.com/JasonLiCSHI/WDEM/releases
DefaultDirName={localappdata}\Programs\WDEM
DefaultGroupName=WDEM
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=WDEM-{#MyAppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\Wdem.App.exe
CloseApplications=yes
RestartApplications=no
ChangesEnvironment=yes
SetupLogging=yes
ShowLanguageDialog=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[CustomMessages]
english.DesktopShortcut=Create a desktop shortcut
chinesesimplified.DesktopShortcut=创建桌面快捷方式
english.ShortcutsGroup=Shortcuts:
chinesesimplified.ShortcutsGroup=快捷方式：
english.AddToPath=Add the WDEM CLI to the user PATH
chinesesimplified.AddToPath=将 WDEM CLI 添加到用户 PATH
english.CommandLineGroup=Command line:
chinesesimplified.CommandLineGroup=命令行：
english.LaunchWdem=Launch WDEM
chinesesimplified.LaunchWdem=启动 WDEM

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; GroupDescription: "{cm:ShortcutsGroup}"; Flags: unchecked
Name: "addtopath"; Description: "{cm:AddToPath}"; GroupDescription: "{cm:CommandLineGroup}"; Flags: checkedonce

[Files]
Source: "{#PublishRoot}\app\Wdem.App.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishRoot}\cli\Wdem.Cli.exe"; DestDir: "{app}"; DestName: "wdem.exe"; Flags: ignoreversion
Source: "{#PublishRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishRoot}\Script\*"; DestDir: "{app}\Script"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishRoot}\Settings\*"; DestDir: "{app}\Settings"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\WDEM"; Filename: "{app}\Wdem.App.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\WDEM"; Filename: "{app}\Wdem.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\WDEM"; ValueType: string; ValueName: "Language"; ValueData: "{language}"; Flags: uninsdeletevalue uninsdeletekeyifempty

[Run]
Filename: "{app}\Wdem.App.exe"; Description: "{cm:LaunchWdem}"; Flags: nowait postinstall skipifsilent

[Code]
function SameDirectory(const Left, Right: string): Boolean;
begin
  Result := CompareText(
    RemoveBackslashUnlessRoot(Trim(Left)),
    RemoveBackslashUnlessRoot(Trim(Right))) = 0;
end;

function PathContains(const CurrentPath, Directory: string): Boolean;
var
  Entries: TStringList;
  Index: Integer;
begin
  Result := False;
  Entries := TStringList.Create;
  try
    Entries.StrictDelimiter := True;
    Entries.Delimiter := ';';
    Entries.DelimitedText := CurrentPath;
    for Index := 0 to Entries.Count - 1 do
      if SameDirectory(Entries[Index], Directory) then
      begin
        Result := True;
        Exit;
      end;
  finally
    Entries.Free;
  end;
end;

procedure AddToUserPath;
var
  CurrentPath: string;
  AppDirectory: string;
begin
  AppDirectory := ExpandConstant('{app}');
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', CurrentPath) then
    CurrentPath := '';

  if not PathContains(CurrentPath, AppDirectory) then
  begin
    if CurrentPath = '' then
      CurrentPath := AppDirectory
    else
      CurrentPath := CurrentPath + ';' + AppDirectory;
    RegWriteExpandStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', CurrentPath);
  end;
end;

procedure RemoveFromUserPath;
var
  CurrentPath: string;
  AppDirectory: string;
  UpdatedPath: string;
  Entries: TStringList;
  Index: Integer;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', CurrentPath) then
    Exit;

  AppDirectory := ExpandConstant('{app}');
  UpdatedPath := '';
  Entries := TStringList.Create;
  try
    Entries.StrictDelimiter := True;
    Entries.Delimiter := ';';
    Entries.DelimitedText := CurrentPath;
    for Index := 0 to Entries.Count - 1 do
      if (Trim(Entries[Index]) <> '') and not SameDirectory(Entries[Index], AppDirectory) then
      begin
        if UpdatedPath <> '' then
          UpdatedPath := UpdatedPath + ';';
        UpdatedPath := UpdatedPath + Trim(Entries[Index]);
      end;
  finally
    Entries.Free;
  end;

  RegWriteExpandStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', UpdatedPath);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    AddToUserPath;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveFromUserPath;
end;
