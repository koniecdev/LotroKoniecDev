; The desktop app's installer (spec 0016). Built by .github/workflows/desktop-release.yml:
;   ISCC /DAppVersion=X.Y.Z /DPublishDir=<dotnet publish folder> /DRepoRoot=<repo root> /O<out folder> LotroPoPolsku.iss
; The AppId ties every version to the same install: never change it.

#ifndef AppVersion
  #error Pass the version with /DAppVersion=X.Y.Z
#endif
#ifndef PublishDir
  #error Pass the dotnet publish folder with /DPublishDir=<folder>
#endif
#ifndef RepoRoot
  #define RepoRoot AddBackslash(SourcePath) + ".."
#endif

[Setup]
AppId={{53881929-2972-43D8-ACE2-1AED0D822D9F}
AppName=LOTRO po Polsku
AppVersion={#AppVersion}
AppVerName=LOTRO po Polsku {#AppVersion}
AppPublisher=LotroKoniecDev
AppPublisherURL=https://github.com/koniecdev/LotroKoniecDev
VersionInfoVersion={#AppVersion}
; All users, in Program Files (x86): the app is x86, so Setup runs in 32-bit mode.
PrivilegesRequired=admin
; Microsoft supports .NET 10 on client Windows from Windows 10 on, so Setup refuses an older Windows.
MinVersion=10.0
DefaultDirName={autopf}\LotroPoPolsku
; No folder page: the app runs as administrator and keeps its settings, log and updates in {app}
; (ADR-0069 amendment of 2026-11-18). That is safe only under Program Files, which only
; administrators can change; a folder such as C:\Games inherits write rights for every user. /DIR=
; and an earlier install's folder can still move it, and both need an administrator. Never add
; PrivilegesRequiredOverridesAllowed: a per-user install puts {autopf} in the player's own profile.
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputBaseFilename=LotroPoPolsku-Setup-{#AppVersion}
SetupIconFile={#RepoRoot}\src\Player\LotroKoniecDev.DesktopApp.Presentation\Assets\app.ico
UninstallDisplayIcon={app}\LotroPoPolsku.exe
UninstallDisplayName=LOTRO po Polsku
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
; An update closes the running app through the Restart Manager. The [Run] entry below starts it
; again, so the Restart Manager does not.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"

[Files]
; The publish folder holds the single-file exe and the native DLLs next to it (datexport.dll, its C
; runtime, Skia, HarfBuzz, ANGLE): the app runs as administrator, so it never unpacks them into the
; player's %TEMP%. \data and \updates are the app's own folders at {app}'s root, never shipped, so
; the uninstaller leaves them alone.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,\data,\updates"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\LOTRO po Polsku"; Filename: "{app}\LotroPoPolsku.exe"
Name: "{autodesktop}\LOTRO po Polsku"; Filename: "{app}\LotroPoPolsku.exe"

[Run]
; runascurrentuser: started by the elevated Setup, so no second prompt and no error 740, because the
; app asks for admin rights (#1464). nowait: Setup must not wait for the app to close. No
; skipifsilent: after a silent update the new version starts on its own.
Filename: "{app}\LotroPoPolsku.exe"; Description: "Uruchom LOTRO po Polsku"; Flags: postinstall nowait runascurrentuser

; No [UninstallDelete]: uninstall removes only what Setup installed. {app}\data (the settings, the
; log, and from #42 the downloaded translation file and its ledger) and {app}\updates stay, and the
; game folder is never touched. Nothing here may delete {app}\updates during an install: the running
; installer of an update sits in it.

[Code]
const
  { Held by the running app (RunningCopyClaim in the Presentation project). Every copy holds the
    Global\ name; the plain one matters only when Windows refuses the global one. CheckForMutexes is
    true when any listed name exists. Not an AppMutex directive: that would also stop a silent update,
    which closes the app itself instead. }
  AppRunningMutex = 'LotroPoPolsku_Running,Global\LotroPoPolsku_Running';

{ The uninstaller cannot remove a running exe, so it asks the player to close the app first. A silent
  uninstall cannot ask, so it stops and removes nothing. The script stays ASCII, so the one Polish
  letter is written as its code (#$0107 is the letter c with an acute accent). }
function InitializeUninstall(): Boolean;
begin
  Result := True;
  while CheckForMutexes(AppRunningMutex) do
  begin
    if UninstallSilent() or
       (MsgBox('LOTRO po Polsku jest uruchomione. Zamknij program i kliknij OK, aby go odinstalowa' + #$0107 +
               ', albo kliknij Anuluj.', mbError, MB_OKCANCEL) <> IDOK) then
    begin
      Result := False;
      Exit;
    end;
  end;
end;
