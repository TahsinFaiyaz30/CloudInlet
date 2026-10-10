#if Ver != 0x06070300
  #error CloudInlet requires Inno Setup 6.7.3.
#endif
#ifndef AppFolder
  #error AppFolder is required
#endif
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef BuildFlavor
  #error BuildFlavor is required
#endif
#if BuildFlavor == "Debug"
  #define ProductName "CloudInlet Debug"
  #define ProductId "{061831F7-6C43-4835-AB26-E4AA777BB4F8}"
  #define StartupName "CloudInletDebug"
#else
  #define ProductName "CloudInlet"
  #define ProductId "{45F4331D-E3BB-4F94-A3F7-CA225B16A8B5}"
  #define StartupName "CloudInlet"
#endif
#define RegistryKey "Software\CloudBay\Distribution\" + BuildFlavor + "\Exe"
#define OtherRegistryKey "Software\CloudBay\Distribution\" + BuildFlavor + "\Msi"

[Setup]
AppId={{#ProductId}
AppName={#ProductName}
AppVersion={#AppVersion}
AppPublisher=CloudInlet
AppPublisherURL=https://github.com/TahsinFaiyaz30/CloudInlet
AppSupportURL=https://github.com/TahsinFaiyaz30/CloudInlet/issues
AppUpdatesURL=https://github.com/TahsinFaiyaz30/CloudInlet/releases
DefaultDirName={localappdata}\Programs\{#ProductName}
DefaultGroupName={#ProductName}
UninstallDisplayIcon={app}\CloudInlet.exe
UninstallDisplayName={#ProductName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
DisableProgramGroupPage=yes
DisableWelcomePage=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
UsePreviousGroup=yes
CloseApplications=no
RestartApplications=no
SetupMutex=CloudBay.Install.{#BuildFlavor}
WizardStyle=modern dynamic
Compression=lzma2/normal
SolidCompression=yes
OutputDir={#OutputDirectory}
OutputBaseFilename={#OutputName}
SetupIconFile={#AppFolder}\Assets\CloudInlet.ico
LicenseFile={#Repository}\LICENSE

[Tasks]
Name: "startup"; Description: "Start {#ProductName} when I sign in to Windows"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#AppFolder}\*"; DestDir: "{app}"; Excludes: "distribution.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MetadataFile}"; DestDir: "{app}"; DestName: "distribution.json"; Flags: ignoreversion
Source: "{#HelperFile}"; DestDir: "{tmp}"; Flags: dontcopy

[Icons]
Name: "{userprograms}\{#ProductName}"; Filename: "{app}\CloudInlet.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\{#ProductName}"; Filename: "{app}\CloudInlet.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; The installed Windows product identity remains stable across CloudInlet updates.
Root: HKCU; Subkey: "{#RegistryKey}"; ValueType: string; ValueName: "InstallDirectory"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "{#RegistryKey}"; ValueType: string; ValueName: "Version"; ValueData: "{#AppVersion}"
Root: HKCU; Subkey: "{#RegistryKey}"; ValueType: string; ValueName: "BuildFlavor"; ValueData: "{#BuildFlavor}"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#StartupName}"; ValueData: """{app}\CloudInlet.exe"" --background"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\CloudInlet.exe"; Description: "Open {#ProductName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  CB_FILE_ATTRIBUTE_REPARSE_POINT = $400;
  CB_FILE_ATTRIBUTE_DIRECTORY = $10;
  CB_INVALID_FILE_ATTRIBUTES = $FFFFFFFF;
  CB_SYNCHRONIZE = $100000;
  CB_WAIT_OBJECT_0 = 0;
  CB_WAIT_TIMEOUT = $102;

function GetFileAttributesW(lpFileName: string): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function OpenProcess(dwDesiredAccess: LongWord; bInheritHandle: Boolean; dwProcessId: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(hHandle: THandle; dwMilliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

var PreviousDirectory: string;
    PreviousStartup, PreviousDesktop, HasPrevious: Boolean;

function IsUpdate: Boolean;
var I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/UPDATE') = 0 then Result := True;
end;

function InitializeSetup: Boolean;
var OtherDirectory: string;
begin
  Result := False;
  if RegQueryStringValue(HKCU, '{#OtherRegistryKey}', 'InstallDirectory', OtherDirectory) then begin
    SuppressibleMsgBox('This build of CloudInlet is installed using MSI. Uninstall that installer before changing to EXE. Your backup settings and files will be preserved.', mbError, MB_OK, IDOK);
    Exit;
  end;
  HasPrevious := RegQueryStringValue(HKCU, '{#RegistryKey}', 'InstallDirectory', PreviousDirectory);
  if IsUpdate and not HasPrevious then begin
    SuppressibleMsgBox('This update requires an existing EXE installation of the same CloudInlet build.', mbError, MB_OK, IDOK);
    Exit;
  end;
  PreviousStartup := RegValueExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#StartupName}');
  PreviousDesktop := FileExists(ExpandConstant('{userdesktop}\{#ProductName}.lnk'));
  Result := True;
end;

procedure InitializeWizard;
var Tasks: string;
begin
  if HasPrevious then begin
    { Published CloudInlet workers verify and reopen this exact directory. }
    WizardForm.DirEdit.Text := PreviousDirectory;
    if PreviousStartup then Tasks := 'startup' else Tasks := '!startup';
    if PreviousDesktop then Tasks := Tasks + ',desktopicon' else Tasks := Tasks + ',!desktopicon';
    WizardSelectTasks(Tasks);
  end;
end;

function ValidInstallDirectory(Path: string): Boolean;
var Parent: string; Attributes: LongWord;
begin
  Result := False;
  if (Length(Path) < 8) or (Copy(Path, 2, 2) <> ':\') or (Pos('"', Path) > 0) then Exit;
  Path := RemoveBackslashUnlessRoot(ExpandFileName(Path));
  // Private state and the Windows directory never belong to an installer.
  if Pos(Lowercase(AddBackslash(ExpandConstant('{localappdata}\CloudBay'))), Lowercase(AddBackslash(Path))) = 1 then Exit;
  if Pos(Lowercase(AddBackslash(ExpandConstant('{localappdata}\CloudInlet'))), Lowercase(AddBackslash(Path))) = 1 then Exit;
  if Pos(Lowercase(AddBackslash(ExpandConstant('{win}'))), Lowercase(AddBackslash(Path))) = 1 then Exit;
  Parent := Path;
  while Parent <> '' do begin
    Attributes := GetFileAttributesW(Parent);
    if (Attributes <> CB_INVALID_FILE_ATTRIBUTES) and
       (((Attributes and CB_FILE_ATTRIBUTE_REPARSE_POINT) <> 0) or
        ((Attributes and CB_FILE_ATTRIBUTE_DIRECTORY) = 0)) then Exit;
    if ExtractFileDir(Parent) = Parent then Break;
    Parent := ExtractFileDir(Parent);
  end;
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
var HelperPath, Tasks: string; Code: Integer;
begin
  Result := '';
  if IsUpdate and HasPrevious then begin
    { Task restoration happens after InitializeWizard for silent setup. Apply
      the current Windows preferences again immediately before installation. }
    if PreviousStartup then Tasks := 'startup' else Tasks := '!startup';
    if PreviousDesktop then Tasks := Tasks + ',desktopicon' else Tasks := Tasks + ',!desktopicon';
    WizardSelectTasks(Tasks);
  end;
  if not ValidInstallDirectory(ExpandConstant('{app}')) then begin
    Result := 'Choose a normal local application directory, outside Windows and CloudInlet private backup settings. Linked directories are not supported.';
    Exit;
  end;
  if HasPrevious and (CompareText(RemoveBackslashUnlessRoot(ExpandConstant('{app}')), RemoveBackslashUnlessRoot(PreviousDirectory)) <> 0) then begin
    Result := 'The update installation directory must match the existing installation.';
    Exit;
  end;
  if HasPrevious and not FileExists(AddBackslash(PreviousDirectory) + 'CloudInlet.exe') then begin
    Result := 'Upgrade CloudBay through its published 1.1.2 bridge first, or repair the existing CloudInlet installation before updating.';
    Exit;
  end;
  ExtractTemporaryFile('CloudInlet.SetupHelper.exe');
  HelperPath := ExpandConstant('{tmp}\CloudInlet.SetupHelper.exe');
  if not Exec(HelperPath, '--flavor {#BuildFlavor} --install-directory "' + RemoveBackslashUnlessRoot(ExpandConstant('{app}')) + '"', ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    Result := 'CloudInlet is still finishing a transfer or did not answer the shutdown request. Quit it from the tray menu, then try installing again.';
end;

function JsonString(Value: string): string;
var I: Integer; C: Char;
begin
  Result := '"';
  for I := 1 to Length(Value) do begin
    C := Value[I];
    case C of
      '"': Result := Result + '\"';
      '\': Result := Result + '\\'; #8: Result := Result + '\b'; #9: Result := Result + '\t';
      { Avoid a leading # token, which ISPP treats as a directive. } #10: Result := Result + '\n'; #12: Result := Result + '\f'; #13: Result := Result + '\r';
    else
      Result := Result + C;
    end;
  end;
  Result := Result + '"';
end;

function JsonBoolean(Value: Boolean): string;
begin
  if Value then Result := 'true' else Result := 'false';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Descriptor: string;
begin
  if CurStep = ssPostInstall then begin
    if not WizardIsTaskSelected('startup') then
      RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#StartupName}');
    if not WizardIsTaskSelected('desktopicon') then
      DeleteFile(ExpandConstant('{userdesktop}\{#ProductName}.lnk'));
    Descriptor := '{"schemaVersion":1,"version":"{#AppVersion}","buildFlavor":"{#BuildFlavor}",' +
      '"installerKind":"Exe","architecture":"x64","installScope":"perUser","installDirectory":' + JsonString(ExpandConstant('{app}')) +
      ',"startWithWindows":' + JsonBoolean(WizardIsTaskSelected('startup')) + ',"desktopShortcut":' + JsonBoolean(WizardIsTaskSelected('desktopicon')) +
      ',"sourceRevision":"{#SourceRevision}"}';
    if not SaveStringToFile(ExpandConstant('{app}\distribution.json'), Utf8Encode(Descriptor), False) then
      RaiseException('The installed distribution metadata could not be written.');
  end;
end;

function InitializeUninstall: Boolean;
var Code: Integer; Image: string;
begin
  Image := ExpandConstant('{app}\CloudInlet.SetupHelper.exe');
  Result := True;
  if FileExists(Image) then
    Result := Exec(Image, '--flavor {#BuildFlavor} --install-directory "' + RemoveBackslashUnlessRoot(ExpandConstant('{app}')) + '" --remove-notifications', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
  if not Result then
    SuppressibleMsgBox('CloudInlet is still safely finishing a transfer. Quit it from the tray menu before uninstalling.', mbError, MB_OK, IDOK);
end;
