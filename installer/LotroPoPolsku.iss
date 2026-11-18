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
; datexport.dll and the C runtime DLLs are inside the single-file exe, so no DLL is listed here.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\LOTRO po Polsku"; Filename: "{app}\LotroPoPolsku.exe"
Name: "{autodesktop}\LOTRO po Polsku"; Filename: "{app}\LotroPoPolsku.exe"

[Run]
; runascurrentuser: started by the elevated Setup, so no second prompt and no error 740 once the app
; asks for admin rights (#42). nowait: Setup must not wait for the app to close. No skipifsilent:
; after a silent update the new version starts on its own.
Filename: "{app}\LotroPoPolsku.exe"; Description: "Uruchom LOTRO po Polsku"; Flags: postinstall nowait runascurrentuser

; No [UninstallDelete]: uninstall removes only what Setup installed. The settings, the downloaded
; translation file and its ledger stay in %AppData%\LotroPoPolsku, and the game folder is never touched.

[Code]
const
  { Held by the running app (Program.RunningMutexName). Not an AppMutex directive: that would also
    stop a silent update, which closes the app itself instead. }
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
