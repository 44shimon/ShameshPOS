; Shmesh POS installer (Inno Setup 6). Build it with scripts\build-installer.ps1, which publishes the app, fetches
; the bundled dependencies and passes their folders:
;   ISCC.exe /DAppVersion=1.0.0 /DSourceDir=..\publish\ShmeshPOS /DPgSqlDir=..\publish\pgsql /DDepsDir=..\publish\deps installer\ShmeshPOS.iss
;
; Everything a clean Windows 10/11 computer needs is inside: the self-contained app, and for the database server
; the PostgreSQL server/tools and the Visual C++ runtime they use.
;
; Setup types:
;  - Database server (a single register, or the main computer): installs PostgreSQL as the Windows service
;    ShmeshPOS-Database with its data in %ProgramData%\ShmeshPOS\pgdata, creates the database and its login with
;    random passwords (saved for administrators in %ProgramData%\ShmeshPOS\database-admin.txt) and connects the
;    POS to it. Optionally lets the other registers on the store network connect.
;  - Register only: asks for the database server's details.
; Both write %ProgramData%\ShmeshPOS\appsettings.json readable only by administrators, SYSTEM and the POS Windows
; account (the file holds the database password). An upgrade keeps that file unless "Change settings" is ticked.
; Uninstalling removes the program and the database service but leaves %ProgramData%\ShmeshPOS (settings, logs,
; backups and the database itself) in place.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\ShmeshPOS"
#endif
#ifndef PgSqlDir
  #define PgSqlDir "..\publish\pgsql"
#endif
#ifndef DepsDir
  #define DepsDir "..\publish\deps"
#endif
#ifndef PgVersion
  #define PgVersion "18"
#endif

#define AppName "Shmesh POS"
#define AppExe "ShmeshPOS.exe"
#define DbService "ShmeshPOS-Database"

[Setup]
; Never change AppId: upgrades find the existing install by it.
AppId={{6F3C2B1E-8A4D-4E7B-9C21-5D0E7A3F9B42}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Shmesh POS
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\ShmeshPOS
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
SetupIconFile=..\src\ShmeshPOS\Resources\ShmeshPOS.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=..\publish\installer
OutputBaseFilename=ShmeshPOS-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
; Close a running Shmesh POS before replacing its files.
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startupicon"; Description: "Start Shmesh POS automatically when Windows starts (registers)"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "setup-database.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
; Database server only.
Source: "{#PgSqlDir}\*"; DestDir: "{app}\pgsql"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsServer
Source: "{#DepsDir}\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: IsServer

[Dirs]
; The POS runs as an ordinary Windows user, which must be able to write its logs and daily backups.
Name: "{commonappdata}\ShmeshPOS"; Flags: uninsneveruninstall
Name: "{commonappdata}\ShmeshPOS\logs"; Permissions: users-modify; Flags: uninsneveruninstall
Name: "{commonappdata}\ShmeshPOS\backups"; Permissions: users-modify; Flags: uninsneveruninstall

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{commonstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startupicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent unchecked

[UninstallRun]
; The database service runs from {app}\pgsql, so it goes with the program. Its data stays in %ProgramData%.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Stop-Service -Name '{#DbService}' -ErrorAction SilentlyContinue; if (Get-Service -Name '{#DbService}' -ErrorAction SilentlyContinue) {{ sc.exe delete '{#DbService}' | Out-Null }; Get-NetFirewallRule -DisplayName 'Shmesh POS Database' -ErrorAction SilentlyContinue | Remove-NetFirewallRule"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveDatabaseService"

[Code]
var
  KeepPage: TInputOptionWizardPage;
  TypePage: TInputOptionWizardPage;
  DbPage: TInputQueryWizardPage;
  RegisterPage: TInputQueryWizardPage;
  OptionsPage: TInputOptionWizardPage;
  AccountPage: TInputQueryWizardPage;
  BackupHost: String;
  LastServerChoice: Integer;
  DbPort: String;
  DbPassword: String;
  DbReady: Boolean;

function DataDir: String;
begin
  Result := ExpandConstant('{commonappdata}\ShmeshPOS');
end;

function ConfigFile: String;
begin
  Result := DataDir + '\appsettings.json';
end;

function AdminFile: String;
begin
  Result := DataDir + '\database-admin.txt';
end;

function HasConfig: Boolean;
begin
  Result := FileExists(ConfigFile);
end;

{ This computer already holds a Shmesh POS database (installed earlier, perhaps since uninstalled). }
function HasDatabase: Boolean;
begin
  Result := FileExists(DataDir + '\pgdata\PG_VERSION');
end;

{ True when the settings pages should be shown: a new register, or "Change settings" was ticked. }
function Configuring: Boolean;
begin
  Result := (not HasConfig) or KeepPage.Values[1];
end;

function ServerChosen: Boolean;
begin
  Result := TypePage.Values[0];
end;

{ The database server is installed (or kept up to date) on this computer. }
function IsServer: Boolean;
begin
  if Configuring then
    Result := ServerChosen
  else
    Result := HasDatabase;
end;

procedure InitializeWizard;
begin
  KeepPage := CreateInputOptionPage(wpSelectTasks,
    'Register settings', 'This computer is already set up.',
    'Settings were found in ' + ConfigFile + '. Keep them when upgrading?',
    True, False);
  KeepPage.Add('Keep the current settings (recommended)');
  KeepPage.Add('Change settings (the current file is replaced)');
  KeepPage.Values[0] := True;

  TypePage := CreateInputOptionPage(KeepPage.ID,
    'Setup type', 'What does this computer do?',
    'All registers share one database. Install it on one computer, then install the other registers as "Register only".',
    True, False);
  TypePage.Add('Database server: install the database on this computer' + #13#10 +
    'For a single register, or the main computer the other registers connect to.');
  TypePage.Add('Register only: connect to the Shmesh POS database on another computer');
  TypePage.Values[0] := True;
  LastServerChoice := -1;

  DbPage := CreateInputQueryPage(TypePage.ID,
    'Database connection', 'Where is the Shmesh POS database?',
    'Enter the database server''s details. They are in ' + AdminFile + ' on that computer ' +
    '(open it as an administrator).');
  DbPage.Add('Server name or IP address:', False);
  DbPage.Add('Port:', False);
  DbPage.Add('Database name:', False);
  DbPage.Add('Database user:', False);
  DbPage.Add('Database password:', True);
  DbPage.Values[0] := '';
  DbPage.Values[1] := '5432';
  DbPage.Values[2] := 'shmesh_pos';
  DbPage.Values[3] := 'shmesh_pos';

  RegisterPage := CreateInputQueryPage(DbPage.ID,
    'This register', 'Name this register and the store.',
    'The register name is printed on receipts and reports, so give each register its own (e.g. REGISTER-1).' + #13#10 +
    'New database only: enter a 4-8 digit PIN for the first administrator. Leave it blank on other registers.');
  RegisterPage.Add('Register name:', False);
  RegisterPage.Add('Store name:', False);
  RegisterPage.Add('First administrator PIN (new database only):', True);
  RegisterPage.Values[0] := Uppercase(GetComputerNameString);
  RegisterPage.Values[1] := 'Shmesh POS';

  OptionsPage := CreateInputOptionPage(RegisterPage.ID,
    'Options', 'How should Shmesh POS run on this computer?', '', False, False);
  OptionsPage.Add('Full screen (kiosk mode): no title bar, Alt+F4 blocked, only an administrator can exit');
  OptionsPage.Add('Create sample products (for a demo or training database only)');
  OptionsPage.Add('Make the daily database backup on this computer (normally the database server only)');
  OptionsPage.Add('Let other registers on the store network connect to this database');

  AccountPage := CreateInputQueryPage(OptionsPage.ID,
    'Protect the settings file', 'Which Windows account runs Shmesh POS?',
    'The settings file holds the database password, so only administrators and this account can read it.' + #13#10 +
    'Enter the Windows user that logs in at this register (e.g. Cashier), or "Users" to allow every local user.');
  AccountPage.Add('Windows account:', False);
  AccountPage.Values[0] := GetUserNameString;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = KeepPage.ID then
    Result := not HasConfig
  else if (PageID = TypePage.ID) or (PageID = RegisterPage.ID) or (PageID = OptionsPage.ID) or (PageID = AccountPage.ID) then
    Result := not Configuring
  else if PageID = DbPage.ID then
    Result := (not Configuring) or ServerChosen;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  { The network option only applies to the database server. }
  if CurPageID = OptionsPage.ID then
  begin
    OptionsPage.CheckListBox.ItemEnabled[3] := ServerChosen;
    if not ServerChosen then
      OptionsPage.Values[3] := False;
  end
  { After a database server install, tell the user how the other registers connect. }
  else if (CurPageID = wpFinished) and DbReady and Configuring and OptionsPage.Values[3] then
  begin
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10 + #13#10 +
      'This computer is now the database server (' + GetComputerNameString + ', port ' + DbPort + '). ' +
      'To add another register, run this setup on it and choose "Register only". The password it asks for is in ' +
      AdminFile + ' (open it as an administrator).';
    WizardForm.AdjustLabelHeight(WizardForm.FinishedLabel);
  end;
end;

function IsDigits(const S: String): Boolean;
var
  I: Integer;
begin
  Result := Length(S) > 0;
  for I := 1 to Length(S) do
    if (S[I] < '0') or (S[I] > '9') then
      Result := False;
end;

{ The same weak PINs the app refuses: one repeated digit (1111) or a straight run (1234, 9876). }
function IsWeakPin(const Pin: String): Boolean;
var
  I, Step: Integer;
  Same, Run: Boolean;
begin
  Same := True;
  Run := True;
  Step := Ord(Pin[2]) - Ord(Pin[1]);
  if (Step <> 1) and (Step <> -1) then
    Run := False;
  for I := 2 to Length(Pin) do
  begin
    if Pin[I] <> Pin[1] then
      Same := False;
    if Ord(Pin[I]) - Ord(Pin[I - 1]) <> Step then
      Run := False;
  end;
  Result := Same or Run;
end;

{ True when the database server named on the database page is this computer. }
function DatabaseIsLocal: Boolean;
var
  Host: String;
begin
  Host := Lowercase(Trim(DbPage.Values[0]));
  Result := (Host = 'localhost') or (Host = '127.0.0.1') or (Host = '::1') or (Host = Lowercase(GetComputerNameString));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port, Choice: Integer;
  Pin: String;
begin
  Result := True;
  if CurPageID = TypePage.ID then
  begin
    { Preset the backup and network boxes when the setup type changes; later changes by the user are kept. }
    if ServerChosen then Choice := 1 else Choice := 0;
    if Choice <> LastServerChoice then
    begin
      LastServerChoice := Choice;
      OptionsPage.Values[2] := ServerChosen;
      OptionsPage.Values[3] := False;
      BackupHost := '';
    end;
  end
  else if CurPageID = DbPage.ID then
  begin
    Port := StrToIntDef(Trim(DbPage.Values[1]), 0);
    if Trim(DbPage.Values[0]) = '' then
    begin
      MsgBox('Enter the database server''s computer name or IP address.', mbError, MB_OK);
      Result := False;
    end
    else if (Port < 1) or (Port > 65535) then
    begin
      MsgBox('Enter a port between 1 and 65535 (it is in database-admin.txt on the database server).', mbError, MB_OK);
      Result := False;
    end
    else if (Trim(DbPage.Values[2]) = '') or (Trim(DbPage.Values[3]) = '') then
    begin
      MsgBox('Enter the database name and user.', mbError, MB_OK);
      Result := False;
    end
    else if DbPage.Values[4] = '' then
      Result := MsgBox('No database password was entered. Continue only if this computer uses a PostgreSQL password file (pgpass.conf).',
        mbConfirmation, MB_YESNO) = IDYES;
    { Backups belong on the database server. Only preset the box when the server changes, so going Back and
      Next again keeps the user's own choice. }
    if Result and (Lowercase(Trim(DbPage.Values[0])) <> BackupHost) then
    begin
      BackupHost := Lowercase(Trim(DbPage.Values[0]));
      OptionsPage.Values[2] := DatabaseIsLocal;
    end;
  end
  else if CurPageID = RegisterPage.ID then
  begin
    Pin := RegisterPage.Values[2];
    if Trim(RegisterPage.Values[0]) = '' then
    begin
      MsgBox('Enter a name for this register.', mbError, MB_OK);
      Result := False;
    end
    else if (Pin = '') and ServerChosen and not HasDatabase then
    begin
      MsgBox('This computer gets a new database, so enter a PIN for the first administrator.', mbError, MB_OK);
      Result := False;
    end
    else if (Pin <> '') and ((Length(Pin) < 4) or (Length(Pin) > 8) or not IsDigits(Pin)) then
    begin
      MsgBox('The administrator PIN must be 4 to 8 digits.', mbError, MB_OK);
      Result := False;
    end
    else if (Pin <> '') and IsWeakPin(Pin) then
    begin
      MsgBox('That PIN is too easy to guess. Avoid repeated digits (1111) and runs (1234).', mbError, MB_OK);
      Result := False;
    end;
  end
  else if CurPageID = AccountPage.ID then
  begin
    if Trim(AccountPage.Values[0]) = '' then
    begin
      MsgBox('Enter the Windows account that runs Shmesh POS, or "Users".', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function PowerShell: String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

{ An upgrade replaces the database server's files, so stop it first; setup starts it again afterwards. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  Exec(PowerShell, '-NoProfile -ExecutionPolicy Bypass -Command "Stop-Service -Name ''{#DbService}'' -ErrorAction SilentlyContinue"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InstallVcRuntime: Boolean;
var
  ResultCode: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Installing the Microsoft Visual C++ runtime...';
  Result := Exec(ExpandConstant('{tmp}\vc_redist.x64.exe'), '/install /quiet /norestart', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
  { 1638: a newer version is already installed. 3010/1641: installed, restart later. }
  Result := Result and ((ResultCode = 0) or (ResultCode = 1638) or (ResultCode = 3010) or (ResultCode = 1641));
  if not Result then
    MsgBox('The Microsoft Visual C++ runtime could not be installed (code ' + IntToStr(ResultCode) + '). ' +
      'The database needs it. Install it from https://aka.ms/vs/17/release/vc_redist.x64.exe and run this setup again.',
      mbError, MB_OK);
end;

{ Runs setup-database.ps1. When configuring, reads back the port and password for the settings file. }
function SetupDatabase: Boolean;
var
  Params, ResultFile, LogFile: String;
  Lines: TArrayOfString;
  I, ResultCode: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Setting up the database (this can take a minute)...';
  ResultFile := ExpandConstant('{tmp}\database-result.txt');
  LogFile := DataDir + '\logs\setup-database.log';
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\tools\setup-database.ps1') + '"' +
    ' -PgDir "' + ExpandConstant('{app}\pgsql') + '" -DataDir "' + DataDir + '\pgdata"' +
    ' -ServiceName "{#DbService}" -LogFile "' + LogFile + '"';
  if Configuring then
  begin
    Params := Params + ' -ExistingConfig "' + ConfigFile + '" -ResultFile "' + ResultFile + '"';
    if OptionsPage.Values[3] then
      Params := Params + ' -AllowNetwork';
  end
  else
    Params := Params + ' -EnsureOnly';

  Result := Exec(PowerShell, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if Result and Configuring then
  begin
    Result := LoadStringsFromFile(ResultFile, Lines);
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      if Pos('port=', Lines[I]) = 1 then DbPort := Copy(Lines[I], 6, Length(Lines[I]));
      if Pos('password=', Lines[I]) = 1 then DbPassword := Copy(Lines[I], 10, Length(Lines[I]));
    end;
    DeleteFile(ResultFile);
    Result := Result and (DbPort <> '') and (DbPassword <> '');
  end;
  if not Result then
    MsgBox('The database could not be set up. The details are in ' + LogFile + '.' + #13#10 + #13#10 +
      'Fix the problem and run this setup again.', mbError, MB_OK);
end;

function JsonEscape(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

function JsonBool(B: Boolean): String;
begin
  if B then Result := 'true' else Result := 'false';
end;

{ Quotes a connection-string value when it contains characters that would break the key=value list. }
function ConnValue(const S: String): String;
begin
  Result := S;
  if (Pos(';', S) > 0) or (Pos('=', S) > 0) or (Pos('''', S) > 0) or (Pos('"', S) > 0) or (Trim(S) <> S) then
  begin
    StringChangeEx(Result, '''', '''''', True);
    Result := '''' + Result + '''';
  end;
end;

procedure WriteConfig;
var
  Host, Port, Database, User, Password, Conn, Json, Account: String;
  Lines: TArrayOfString;
  ResultCode: Integer;
begin
  if ServerChosen then
  begin
    Host := 'localhost';
    Port := DbPort;
    Database := 'shmesh_pos';
    User := 'shmesh_pos';
    Password := DbPassword;
  end
  else
  begin
    Host := Trim(DbPage.Values[0]);
    Port := Trim(DbPage.Values[1]);
    Database := Trim(DbPage.Values[2]);
    User := Trim(DbPage.Values[3]);
    Password := DbPage.Values[4];
  end;

  Conn := 'Host=' + ConnValue(Host) + ';Port=' + Port + ';Database=' + ConnValue(Database) + ';Username=' + ConnValue(User);
  if Password <> '' then
    Conn := Conn + ';Password=' + ConnValue(Password);
  Conn := Conn + ';Application Name=Shmesh POS;Timeout=10';

  Json :=
    '{' + #13#10 +
    '  "ConnectionStrings": {' + #13#10 +
    '    "ShmeshPos": "' + JsonEscape(Conn) + '"' + #13#10 +
    '  },' + #13#10 +
    '  "Pos": {' + #13#10 +
    '    "StoreName": "' + JsonEscape(Trim(RegisterPage.Values[1])) + '",' + #13#10 +
    '    "TerminalName": "' + JsonEscape(Trim(RegisterPage.Values[0])) + '",' + #13#10 +
    '    "FullScreen": ' + JsonBool(OptionsPage.Values[0]) + #13#10 +
    '  },' + #13#10 +
    '  "Seed": {' + #13#10 +
    '    "AdminPin": "' + RegisterPage.Values[2] + '",' + #13#10 +
    '    "SampleData": ' + JsonBool(OptionsPage.Values[1]) + #13#10 +
    '  },' + #13#10 +
    '  "Backup": {' + #13#10 +
    '    "Enabled": ' + JsonBool(OptionsPage.Values[2]) + #13#10 +
    '  }' + #13#10 +
    '}';

  { UTF-8, so store names like "Café" survive. }
  SetArrayLength(Lines, 1);
  Lines[0] := Json;
  if not SaveStringsToUTF8File(ConfigFile, Lines, False) then
  begin
    MsgBox('Could not write ' + ConfigFile + '. Create it by hand from register-appsettings.example.json.', mbError, MB_OK);
    Exit;
  end;

  { Only administrators, SYSTEM and the POS account may read it: it holds the database password. }
  Account := Trim(AccountPage.Values[0]);
  if not Exec(ExpandConstant('{sys}\icacls.exe'),
      '"' + ConfigFile + '" /inheritance:r /grant:r *S-1-5-32-544:F *S-1-5-18:F "' + Account + '":R',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    MsgBox('The settings file was saved, but its permissions could not be set for "' + Account + '".' + #13#10 +
      'Check the account name, then restrict ' + ConfigFile + ' by hand (right-click → Properties → Security).',
      mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then
    Exit;
  if IsServer then
  begin
    if not InstallVcRuntime then
      Exit;
    DbReady := SetupDatabase;
    if not DbReady then
      Exit;
  end;
  if Configuring then
    WriteConfig;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo,
  MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine;
  if MemoTasksInfo <> '' then
    Result := Result + MemoTasksInfo + NewLine + NewLine;
  if Configuring then
  begin
    Result := Result + 'Register settings:' + NewLine;
    if ServerChosen then
    begin
      Result := Result + Space + 'Database: on this computer (PostgreSQL {#PgVersion}, Windows service {#DbService})' + NewLine;
      if HasDatabase then
        Result := Result + Space + 'Existing database in ' + DataDir + '\pgdata is kept' + NewLine;
      Result := Result + Space + 'Other registers may connect: ' + JsonBool(OptionsPage.Values[3]) + NewLine;
    end
    else
      Result := Result + Space + 'Database: ' + Trim(DbPage.Values[3]) + '@' + Trim(DbPage.Values[0]) + ':' +
        Trim(DbPage.Values[1]) + '/' + Trim(DbPage.Values[2]) + NewLine;
    Result := Result +
      Space + 'Register: ' + Trim(RegisterPage.Values[0]) + NewLine +
      Space + 'Full screen: ' + JsonBool(OptionsPage.Values[0]) + NewLine +
      Space + 'Daily backup on this computer: ' + JsonBool(OptionsPage.Values[2]) + NewLine +
      Space + 'Settings readable by: ' + Trim(AccountPage.Values[0]);
  end
  else
  begin
    Result := Result + 'Register settings: kept as they are.';
    if HasDatabase then
      Result := Result + NewLine + 'The database on this computer is kept and updated.';
  end;
end;
