; Shmesh POS installer (Inno Setup 6). Build it with scripts\build-installer.ps1, which publishes the app and
; passes AppVersion and SourceDir:
;   ISCC.exe /DAppVersion=1.0.0 /DSourceDir=..\publish\ShmeshPOS installer\ShmeshPOS.iss
;
; A fresh install asks for the database connection and this register's details, then writes
; %ProgramData%\ShmeshPOS\appsettings.json readable only by administrators, SYSTEM and the POS Windows account
; (the file holds the database password). An upgrade keeps that file unless "Change settings" is ticked.
; Uninstalling leaves %ProgramData%\ShmeshPOS (settings, logs, backups) in place.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\ShmeshPOS"
#endif

#define AppName "Shmesh POS"
#define AppExe "ShmeshPOS.exe"

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

[Code]
var
  KeepPage: TInputOptionWizardPage;
  DbPage: TInputQueryWizardPage;
  RegisterPage: TInputQueryWizardPage;
  OptionsPage: TInputOptionWizardPage;
  AccountPage: TInputQueryWizardPage;
  BackupHost: String;

function ConfigFile: String;
begin
  Result := ExpandConstant('{commonappdata}\ShmeshPOS\appsettings.json');
end;

function HasConfig: Boolean;
begin
  Result := FileExists(ConfigFile);
end;

{ True when the settings pages should be shown: a new register, or "Change settings" was ticked. }
function Configuring: Boolean;
begin
  Result := (not HasConfig) or KeepPage.Values[1];
end;

procedure InitializeWizard;
begin
  KeepPage := CreateInputOptionPage(wpSelectTasks,
    'Register settings', 'This register is already set up.',
    'Settings were found in ' + ConfigFile + '. Keep them when upgrading?',
    True, False);
  KeepPage.Add('Keep the current settings (recommended)');
  KeepPage.Add('Change settings (the current file is replaced)');
  KeepPage.Values[0] := True;

  DbPage := CreateInputQueryPage(KeepPage.ID,
    'Database connection', 'Where is the Shmesh POS database?',
    'Enter the PostgreSQL server that all registers share. Use "localhost" when the database is on this computer.');
  DbPage.Add('Server name or IP address:', False);
  DbPage.Add('Port:', False);
  DbPage.Add('Database name:', False);
  DbPage.Add('Database user:', False);
  DbPage.Add('Database password:', True);
  DbPage.Values[0] := 'localhost';
  DbPage.Values[1] := '5432';
  DbPage.Values[2] := 'shmesh_pos';
  DbPage.Values[3] := 'shmesh_pos';

  RegisterPage := CreateInputQueryPage(DbPage.ID,
    'This register', 'Name this register and the store.',
    'The register name is printed on receipts and reports, so give each register its own (e.g. REGISTER-1).' + #13#10 +
    'First register only: enter a 4-8 digit PIN to create the first administrator. Leave it blank on other registers.');
  RegisterPage.Add('Register name:', False);
  RegisterPage.Add('Store name:', False);
  RegisterPage.Add('First administrator PIN (first register only):', True);
  RegisterPage.Values[0] := Uppercase(GetComputerNameString);
  RegisterPage.Values[1] := 'Shmesh POS';

  OptionsPage := CreateInputOptionPage(RegisterPage.ID,
    'Register options', 'How should Shmesh POS run on this computer?', '', False, False);
  OptionsPage.Add('Full screen (kiosk mode): no title bar, Alt+F4 blocked, only an administrator can exit');
  OptionsPage.Add('Create sample products (for a demo or training database only)');
  OptionsPage.Add('Make the daily database backup on this computer (needs the PostgreSQL tools; normally the server only)');

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
  else if (PageID = DbPage.ID) or (PageID = RegisterPage.ID) or (PageID = OptionsPage.ID) or (PageID = AccountPage.ID) then
    Result := not Configuring;
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
  Port: Integer;
  Pin: String;
begin
  Result := True;
  if CurPageID = DbPage.ID then
  begin
    Port := StrToIntDef(Trim(DbPage.Values[1]), 0);
    if Trim(DbPage.Values[0]) = '' then
    begin
      MsgBox('Enter the database server name or IP address.', mbError, MB_OK);
      Result := False;
    end
    else if (Port < 1) or (Port > 65535) then
    begin
      MsgBox('Enter a port between 1 and 65535 (PostgreSQL normally uses 5432).', mbError, MB_OK);
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
    { Backups belong on the database server; registers that only connect to it don't make them. Only preset the
      box when the server changes, so going Back and Next again keeps the user's own choice. }
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
  Conn, Json, Account: String;
  Lines: TArrayOfString;
  ResultCode: Integer;
begin
  Conn := 'Host=' + ConnValue(Trim(DbPage.Values[0])) + ';Port=' + Trim(DbPage.Values[1]) +
    ';Database=' + ConnValue(Trim(DbPage.Values[2])) + ';Username=' + ConnValue(Trim(DbPage.Values[3]));
  if DbPage.Values[4] <> '' then
    Conn := Conn + ';Password=' + ConnValue(DbPage.Values[4]);
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
  if (CurStep = ssPostInstall) and Configuring then
    WriteConfig;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo,
  MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine;
  if MemoTasksInfo <> '' then
    Result := Result + MemoTasksInfo + NewLine + NewLine;
  if Configuring then
    Result := Result + 'Register settings:' + NewLine +
      Space + 'Database: ' + Trim(DbPage.Values[3]) + '@' + Trim(DbPage.Values[0]) + ':' + Trim(DbPage.Values[1]) +
        '/' + Trim(DbPage.Values[2]) + NewLine +
      Space + 'Register: ' + Trim(RegisterPage.Values[0]) + NewLine +
      Space + 'Full screen: ' + JsonBool(OptionsPage.Values[0]) + NewLine +
      Space + 'Daily backup on this computer: ' + JsonBool(OptionsPage.Values[2]) + NewLine +
      Space + 'Settings readable by: ' + Trim(AccountPage.Values[0])
  else
    Result := Result + 'Register settings: kept as they are.';
end;
