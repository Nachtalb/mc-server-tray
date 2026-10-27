; Inno Setup script for McServerTray.
; Build: iscc /DAppVersion=1.0.0 /DPublishDir=..\publish installer\McServerTray.iss
; Installs per user (no admin). If the .NET 9 Desktop Runtime is missing it is downloaded from
; Microsoft and installed first (that step asks for admin, since the runtime is per machine).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

#define AppName "McServerTray"
#define AppExe "McServerTray.exe"
#define DotNetUrl "https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe"

[Setup]
AppId={{CFD3686C-759D-4B22-B9B6-A06937FABCF2}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Nachtalb
AppPublisherURL=https://github.com/Nachtalb/mc-server-tray
AppSupportURL=https://github.com/Nachtalb/mc-server-tray/issues
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename={#AppName}-{#AppVersion}-setup
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#AppExe}
WizardStyle=modern
; Own artwork from tools/make-installer-art.ps1, at 100/150/200% DPI.
WizardImageFile=wizard-100.bmp,wizard-150.bmp,wizard-200.bmp
WizardSmallImageFile=wizard-small-100.bmp,wizard-small-150.bmp,wizard-small-200.bmp
WizardImageStretch=no
; $BBGGRR: the darkest dirt shade, should the panel be taller than the image.
WizardImageBackColor=$111924
Compression=lzma2/max
SolidCompression=yes
; The app holds this mutex while running; setup and uninstall ask to close it first.
AppMutex={#AppName}
CloseApplications=yes

[Tasks]
Name: startup; Description: "Start {#AppName} with Windows"
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Same value the app's own "Start with Windows" toggle uses.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"""; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--show"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\{#AppName}\toast"

[Code]
var
  DownloadPage: TDownloadWizardPage;

{ The runtime lives in <Program Files>\dotnet\shared\Microsoft.WindowsDesktop.App\<version>. }
function HasDesktopRuntime9: Boolean;
var
  Find: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\9.*'), Find);
  if Result then
    FindClose(Find);
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage('Downloading .NET 9 Desktop Runtime',
    '{#AppName} needs the .NET 9 Desktop Runtime, which is not installed yet.', nil);
end;

function InstallDesktopRuntime: Boolean;
var
  ResultCode: Integer;
begin
  DownloadPage.Clear;
  DownloadPage.Add('{#DotNetUrl}', 'windowsdesktop-runtime-9-win-x64.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      SuppressibleMsgBox(AddPeriod(GetExceptionMessage), mbCriticalError, MB_OK, IDOK);
      Result := False;
      exit;
    end;
  finally
    DownloadPage.Hide;
  end;

  { The runtime installs per machine, so it needs elevation even though this setup doesn't. }
  if not ShellExec('runas', ExpandConstant('{tmp}\windowsdesktop-runtime-9-win-x64.exe'),
    '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    ResultCode := -1;
  { 3010 = installed, reboot recommended }
  Result := (ResultCode = 0) or (ResultCode = 3010);
  if not Result then
    SuppressibleMsgBox('Installing the .NET 9 Desktop Runtime failed (code ' + IntToStr(ResultCode) + ').' + #13#10 +
      'You can install it from https://dotnet.microsoft.com/download/dotnet/9.0 and run this setup again.',
      mbError, MB_OK, IDOK);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and not HasDesktopRuntime9 then
    Result := InstallDesktopRuntime;
end;

{ The app can enable autostart itself, so clean that up too, not just the installer's task,
  but only when it points at this copy (not e.g. a separately built one). }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value: String;
begin
  if (CurUninstallStep = usUninstall) and
     RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}', Value) and
     (Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Value)) > 0) then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}');
end;
