; Minutar — Inno Setup installer (per-user, fără UAC; autostart ne-elevat prin Startup).
; Compilat de CI (.github/workflows/release.yml) cu: iscc /DAppVersion=x.y.z minutar.iss
; Așteaptă binarele publish-uite în installer\staging\ (vezi workflow-ul).

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif

[Setup]
AppId={{7A1B7A70-5C1E-4B7D-9C7E-3A61F2B0D4E1}
AppName=Minutar
AppVersion={#AppVersion}
AppPublisher=Iustin Aionese
AppPublisherURL=https://github.com/agentuldigital-AI/minutar
AppSupportURL=https://github.com/agentuldigital-AI/minutar/issues
DefaultDirName={localappdata}\time-tracker
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=Minutar-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Minutar
UninstallDisplayIcon={app}\bin\Tracker.Supervisor\Tracker.Supervisor.exe
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; binarele publish-uite (framework-dependent, win-x64)
Source: "staging\bin\*"; DestDir: "{app}\bin"; Flags: recursesubdirs ignoreversion
; extensia de browser (load unpacked până la Web Store)
Source: "staging\extension\*"; DestDir: "{app}\extension"; Flags: recursesubdirs ignoreversion
; șablonul de config — DOAR la prima instalare; nu se atinge la upgrade/uninstall
Source: "staging\config\tracker.toml"; DestDir: "{localappdata}\TimeTracker"; \
  Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
; autostart NE-elevat: shortcut în folderul Startup al userului (zero UAC)
Name: "{userstartup}\Minutar"; Filename: "{app}\bin\Tracker.Supervisor\Tracker.Supervisor.exe"
Name: "{userprograms}\Minutar"; Filename: "{app}\bin\Tracker.Supervisor\Tracker.Supervisor.exe"
Name: "{userprograms}\Minutar Dashboard"; Filename: "http://localhost:5601"

[Run]
Filename: "{app}\bin\Tracker.Supervisor\Tracker.Supervisor.exe"; \
  Description: "Pornește Minutar acum"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "taskkill"; Parameters: "/F /IM Tracker.Supervisor.exe /IM Tracker.Daemon.exe /IM Tracker.Watcher.exe"; \
  Flags: runhidden skipifdoesntexist; RunOnceId: "KillMinutar"

[UninstallDelete]
; log-urile și starea UI; datele (%LOCALAPPDATA%\TimeTracker) rămân ale userului
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{app}\coach"

[Code]
const
  RuntimeUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe';

var
  NeedsRuntime: Boolean;

function DesktopRuntime10Present(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  // framework-dependent apphosts caută runtime-ul mașină-wide
  if RegGetSubkeyNames(HKLM64,
      'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App',
      Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 3) = '10.' then
      begin
        Result := True;
        exit;
      end;
end;

procedure KillRunning();
var
  R: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'),
    '/F /IM Tracker.Supervisor.exe /IM Tracker.Daemon.exe /IM Tracker.Watcher.exe',
    '', SW_HIDE, ewWaitUntilTerminated, R);
  Sleep(800);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillRunning();
  Result := '';
end;

procedure InitializeWizard();
begin
  NeedsRuntime := not DesktopRuntime10Present();
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  R: Integer;
begin
  Result := True;
  if (CurPageID = wpReady) and NeedsRuntime then
  begin
    DownloadTemporaryFile(RuntimeUrl, 'windowsdesktop-runtime.exe', '', nil);
    // instalarea runtime-ului e mașină-wide: singurul prompt UAC din tot setup-ul,
    // o singură dată per calculator
    if not Exec(ExpandConstant('{tmp}\windowsdesktop-runtime.exe'),
        '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, R) then
    begin
      MsgBox('Instalarea .NET Desktop Runtime a eșuat. Instalează-l manual de la ' + RuntimeUrl
        + ' și rulează din nou acest setup.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;
