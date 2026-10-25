; Installer per utente di Battery Forecast (x64 o arm64). Compilato da tools\package.ps1:
;   ISCC.exe /DAppVersion=0.9.0-alpha.1 /DDist=..\..\artifacts\BatteryForecast windows\packaging\BatteryForecast.iss
; Nessun privilegio amministrativo; tutto offline: app .NET autonoma, motore Python, pesi e licenze.

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef Dist
  #define Dist "..\..\artifacts\BatteryForecast"
#endif

[Setup]
; AppId stabile: non cambiarlo, serve agli aggiornamenti in-place e alla disinstallazione
AppId={{868AE5C4-C079-4FE5-972C-C28E6A348E2D}
AppName=Battery Forecast
AppVersion={#AppVersion}
AppVerName=Battery Forecast {#AppVersion}
AppPublisher=Battery Forecast contributors
AppPublisherURL=https://github.com/Mattboxx/battery-forecast
AppSupportURL=https://github.com/Mattboxx/battery-forecast/issues
AppCopyright=Till Dusk by Matteo Bonfanti (MIT); Windows port contributors
PrivilegesRequired=lowest
DefaultDirName={autopf}\Battery Forecast
; stesso AppId dell'alpha "Till Dusk": la sostituisce, ma in una cartella con il nuovo nome
UsePreviousAppDir=no
DisableProgramGroupPage=yes
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.19041
OutputDir=..\..\artifacts
OutputBaseFilename=BatteryForecast-Setup-{#AppVersion}-{#Arch}
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\BatteryForecast.exe
UninstallDisplayName=Battery Forecast
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4
; l'app si chiude con --quit (PrepareToInstall); Restart Manager resta come riserva
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
it.StartupTask=Avvia Battery Forecast all'accesso a Windows (lo storico si registra solo mentre l'app è aperta)
en.StartupTask=Start Battery Forecast when I sign in to Windows (history is recorded only while the app runs)
it.DesktopTask=Crea un collegamento sul desktop
en.DesktopTask=Create a desktop shortcut
it.RemoveData=Eliminare anche lo storico della batteria e le preferenze di Battery Forecast?%n%nSe scegli No, restano in %1 e verranno usati da una futura installazione.
en.RemoveData=Also delete Battery Forecast's battery history and preferences?%n%nIf you choose No, they stay in %1 and a future installation will use them.
it.CloseFailed=Battery Forecast è ancora in esecuzione. Chiudilo dall'icona nell'area di notifica (Esci) e riprova.
en.CloseFailed=Battery Forecast is still running. Quit it from the notification area icon and try again.

[Tasks]
Name: "startup"; Description: "{cm:StartupTask}"
Name: "desktopicon"; Description: "{cm:DesktopTask}"; Flags: unchecked

[Files]
Source: "{#Dist}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[InstallDelete]
; un aggiornamento non deve lasciare librerie vecchie del motore accanto alle nuove
Type: filesandordirs; Name: "{app}\engine\_internal"
; installazione precedente con il vecchio nome (0.9.0-alpha.1)
Type: filesandordirs; Name: "{autopf}\Till Dusk"
Type: files; Name: "{userprograms}\Till Dusk.lnk"
Type: files; Name: "{autodesktop}\Till Dusk.lnk"

[Icons]
Name: "{userprograms}\Battery Forecast"; Filename: "{app}\BatteryForecast.exe"
Name: "{autodesktop}\Battery Forecast"; Filename: "{app}\BatteryForecast.exe"; Tasks: desktopicon

[Registry]
; stesso valore che scrive l'app da Preferenze → Avvia all'accesso
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "BatteryForecast"; \
  ValueData: """{app}\BatteryForecast.exe"" --background"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\BatteryForecast.exe"; Description: "{cm:LaunchProgram,Battery Forecast}"; Flags: nowait postinstall skipifsilent
; aggiornamento dall'app (installazione /SILENT): l'app si riapre da sola, senza pannello
Filename: "{app}\BatteryForecast.exe"; Parameters: "--background"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "{app}\BatteryForecast.exe"; Parameters: "--quit"; RunOnceId: "QuitBatteryForecast"; Flags: runhidden waituntilterminated skipifdoesntexist

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
  Code: Integer;
begin
  Result := '';
  Exe := ExpandConstant('{app}\BatteryForecast.exe');
  if FileExists(Exe) then
    if Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code <> 0) then
      Result := CustomMessage('CloseFailed');
  // l'alpha con il vecchio nome va chiusa prima di cancellarne la cartella
  Exe := ExpandConstant('{autopf}\Till Dusk\TillDusk.exe');
  if (Result = '') and FileExists(Exe) then
    if Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code <> 0) then
      Result := CustomMessage('CloseFailed');
  RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'TillDusk');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // l'avvio all'accesso può averlo impostato anche l'app, non solo l'installer
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'BatteryForecast');
    Data := ExpandConstant('{localappdata}\BatteryForecast');
    if DirExists(Data) and not UninstallSilent then
      if MsgBox(FmtMessage(CustomMessage('RemoveData'), [Data]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Data, True, True, True);
  end;
end;
