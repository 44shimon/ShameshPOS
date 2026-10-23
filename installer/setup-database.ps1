<#
.SYNOPSIS
    Sets up the PostgreSQL server bundled with Shmesh POS. The installer runs it as an administrator.

.DESCRIPTION
    First run: creates the database cluster in -DataDir with a random superuser password, registers and starts
    it as a Windows service, and creates the Shmesh POS login and database with a random password. Later runs
    keep the cluster, its port and the login's password (read from -ExistingConfig, so other registers keep
    working) and re-apply the network setting.
    Both accounts are saved in database-admin.txt next to the data folder, readable by administrators only: the
    superuser is needed to restore backups, the login to set up other registers.
    Writes "port=" and "password=" lines to -ResultFile for the installer's settings file.

    -EnsureOnly (upgrade that keeps the settings): only makes sure the service is registered and running.

    Must run on Windows PowerShell 5.1, which every supported Windows has. Passwords are never logged.
#>
param(
    [Parameter(Mandatory = $true)][string]$PgDir,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [string]$ServiceName = "ShmeshPOS-Database",
    [string]$Database = "shmesh_pos",
    [string]$AppUser = "shmesh_pos",
    [switch]$AllowNetwork,
    [switch]$EnsureOnly,
    [string]$ExistingConfig,
    [string]$ResultFile,
    [string]$LogFile
)

$ErrorActionPreference = "Stop"
$bin = Join-Path $PgDir "bin"
$adminFile = Join-Path (Split-Path $DataDir -Parent) "database-admin.txt"
$settingsFile = Join-Path $DataDir "shmeshpos.conf"
$firewallRule = "Shmesh POS Database"
$networkHbaMarker = "# shmeshpos-network"

function Write-Log([string]$Message) {
    $line = "{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $Message
    Write-Host $line
    if ($LogFile) { Add-Content -Path $LogFile -Value $line -Encoding UTF8 }
}

# Runs a PostgreSQL tool, logs its output and throws on a non-zero exit code. Windows PowerShell turns native
# stderr into errors when ErrorActionPreference is Stop, so relax it for the call.
function Invoke-Pg([string]$Tool, [string[]]$Arguments) {
    $exe = Join-Path $bin $Tool
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { $output = & $exe @Arguments 2>&1 | ForEach-Object { "$_" } }
    finally { $ErrorActionPreference = $previous }
    foreach ($line in $output) { Write-Log "  $line" }
    if ($LASTEXITCODE -ne 0) { throw "$Tool failed (exit code $LASTEXITCODE). See the lines above." }
}

function New-Password {
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    ($bytes | ForEach-Object { $_.ToString("x2") }) -join ""
}

# Free when nothing listens on it, on loopback or on all addresses.
function Test-PortFree([int]$Port) {
    foreach ($address in @([System.Net.IPAddress]::Loopback, [System.Net.IPAddress]::Any)) {
        $listener = New-Object System.Net.Sockets.TcpListener($address, $Port)
        try { $listener.Start() } catch { return $false } finally { $listener.Stop() }
    }
    return $true
}

# Only administrators and SYSTEM, plus (for the data folder) the account the service runs as.
function Protect-Path([string]$Path, [switch]$ForService) {
    $grants = @("*S-1-5-32-544:(OI)(CI)F", "*S-1-5-18:(OI)(CI)F")
    if ($ForService) { $grants += "*S-1-5-20:(OI)(CI)F" }   # NETWORK SERVICE
    if (-not (Test-Path $Path -PathType Container)) { $grants = $grants -replace "\(OI\)\(CI\)", "" }
    $output = & icacls.exe $Path /inheritance:r /grant:r @grants /C /Q 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Could not set permissions on ${Path}: $output" }
}

function Read-AdminFile([string]$Key) {
    if (-not (Test-Path $adminFile)) { return $null }
    $match = Select-String -Path $adminFile -Pattern ("^" + [regex]::Escape($Key) + ":\s*(\S+)") | Select-Object -First 1
    if ($match) { return $match.Matches[0].Groups[1].Value }
    return $null
}

function Write-AdminFile([int]$Port, [string]$SuperPassword, [string]$AppPassword) {
    Set-Content -Path $adminFile -Encoding ASCII -Value @(
        "Shmesh POS database accounts. Keep this file safe; only administrators can open it.",
        "",
        "Database server: $env:COMPUTERNAME",
        "Port: $Port",
        "Database name: $Database",
        "",
        "Other registers connect with these (enter them in the installer on each register):",
        "Register user: $AppUser",
        "Register password: $AppPassword",
        "",
        "Administrator account, for restoring backups:",
        "Superuser: postgres",
        "Superuser password: $SuperPassword"
    )
    Protect-Path $adminFile
}

# The password in this computer's existing settings file, so "Change settings" doesn't lock out other registers.
function Get-ExistingAppPassword {
    if (-not $ExistingConfig -or -not (Test-Path $ExistingConfig)) { return $null }
    try {
        $connection = (Get-Content $ExistingConfig -Raw | ConvertFrom-Json).ConnectionStrings.ShmeshPos
        $builder = New-Object System.Data.Common.DbConnectionStringBuilder
        $builder.ConnectionString = $connection
        if ($builder.ContainsKey("Password")) { return [string]$builder["Password"] }
    } catch {
        Write-Log "Could not read the password from ${ExistingConfig}: $($_.Exception.Message)"
    }
    return $null
}

function Get-ClusterPort {
    if (Test-Path $settingsFile) {
        $match = Select-String -Path $settingsFile -Pattern "^\s*port\s*=\s*(\d+)" | Select-Object -Last 1
        if ($match) { return [int]$match.Matches[0].Groups[1].Value }
    }
    return 5432
}

# Our settings live in their own file, included at the end of postgresql.conf, so they can be rewritten safely.
function Write-ClusterSettings([int]$Port) {
    $listen = if ($AllowNetwork) { "*" } else { "localhost" }
    Set-Content -Path $settingsFile -Encoding ASCII -Value @(
        "# Written by the Shmesh POS installer; changes here are replaced on the next install.",
        "port = $Port",
        "listen_addresses = '$listen'",
        "logging_collector = on",
        "log_filename = 'postgresql-%a.log'",
        "log_truncate_on_rotation = on",
        "log_rotation_age = 1d"
    )

    $conf = Join-Path $DataDir "postgresql.conf"
    if (-not (Select-String -Path $conf -Pattern "shmeshpos\.conf" -Quiet)) {
        Add-Content -Path $conf -Value "`r`ninclude_if_exists = 'shmeshpos.conf'" -Encoding ASCII
    }

    # Other registers on the store network (any subnet this computer is on) may log in with a password.
    $hba = Join-Path $DataDir "pg_hba.conf"
    $lines = @(Get-Content $hba | Where-Object { $_ -notmatch [regex]::Escape($networkHbaMarker) })
    if ($AllowNetwork) { $lines += "host    all             all             samenet                 scram-sha-256 $networkHbaMarker" }
    Set-Content -Path $hba -Value $lines -Encoding ASCII
}

function Set-Firewall([int]$Port) {
    Get-NetFirewallRule -DisplayName $firewallRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    if ($AllowNetwork) {
        New-NetFirewallRule -DisplayName $firewallRule -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port `
            -Profile Domain, Private -Program (Join-Path $bin "postgres.exe") | Out-Null
        Write-Log "Firewall: registers on private and domain networks may connect on port $Port."
    }
}

function Start-Cluster([int]$Port) {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $service) {
        Write-Log "Registering Windows service $ServiceName..."
        Invoke-Pg "pg_ctl.exe" @("register", "-N", $ServiceName, "-U", "NT AUTHORITY\NetworkService",
            "-D", $DataDir, "-S", "auto", "-w")
        & sc.exe description $ServiceName "Database for Shmesh POS (PostgreSQL). Registers on this computer and the network use it." | Out-Null
        & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
        $service = Get-Service -Name $ServiceName
    }
    if ($service.Status -eq "Running") {
        Write-Log "Restarting $ServiceName to apply settings..."
        Restart-Service -Name $ServiceName
    } else {
        Write-Log "Starting $ServiceName..."
        Start-Service -Name $ServiceName
    }

    for ($i = 0; $i -lt 60; $i++) {
        & (Join-Path $bin "pg_isready.exe") -h localhost -p $Port -q
        if ($LASTEXITCODE -eq 0) { Write-Log "Database server is ready on port $Port."; return }
        Start-Sleep -Seconds 1
    }
    throw "The database server did not start. See the logs in $(Join-Path $DataDir 'log') and the Windows event log."
}

try {
    Write-Log "Shmesh POS database setup: $DataDir"
    $exists = Test-Path (Join-Path $DataDir "PG_VERSION")

    if ($EnsureOnly) {
        if (-not $exists) { throw "No database was found in $DataDir. Run the installer again and choose 'Change settings'." }
        Start-Cluster (Get-ClusterPort)
        exit 0
    }

    if ($exists) {
        $port = Get-ClusterPort
        $superPassword = Read-AdminFile "Superuser password"
        if (-not $superPassword) { throw "The superuser password in $adminFile is missing, so the database can't be changed." }
        Write-Log "Keeping the existing database (port $port)."
    } else {
        $port = 5432
        while (-not (Test-PortFree $port)) {
            if ($port -ge 5450) { throw "No free port between 5432 and 5450 for the database server." }
            $port++
        }
        $superPassword = New-Password
        # Saved before initdb, so the password can't be lost if a later step fails.
        Write-AdminFile $port $superPassword ""

        Write-Log "Creating the database cluster (port $port)..."
        $pwFile = Join-Path $env:TEMP ("shmeshpos-" + [guid]::NewGuid() + ".tmp")
        try {
            Set-Content -Path $pwFile -Value $superPassword -Encoding ASCII -NoNewline
            # Builtin C.UTF-8 locale: UTF-8 text that sorts the same whatever the Windows language.
            Invoke-Pg "initdb.exe" @("-D", $DataDir, "-U", "postgres", "--pwfile=$pwFile", "-E", "UTF8",
                "--locale=C", "--locale-provider=builtin", "--builtin-locale=C.UTF-8", "--auth=scram-sha-256")
        } finally {
            Remove-Item $pwFile -Force -ErrorAction SilentlyContinue
        }
        # After initdb, which runs with a restricted token and needs the installing user's own access.
        Protect-Path $DataDir -ForService
    }

    Write-ClusterSettings $port
    Set-Firewall $port
    Start-Cluster $port

    $appPassword = Get-ExistingAppPassword
    if ($appPassword) { Write-Log "Keeping the register password from the current settings." }
    else { $appPassword = Read-AdminFile "Register password" }
    if (-not $appPassword) { $appPassword = New-Password }

    Write-Log "Creating the $AppUser login and the $Database database..."
    $sql = Join-Path $env:TEMP ("shmeshpos-" + [guid]::NewGuid() + ".sql")
    Set-Content -Path $sql -Encoding ASCII -Value @(
        "\getenv app_password SHMESH_APP_PASSWORD",
        "SELECT format('CREATE ROLE %I LOGIN', :'app_user') WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'app_user') \gexec",
        "SELECT format('ALTER ROLE %I WITH LOGIN PASSWORD %L', :'app_user', :'app_password') \gexec",
        "SELECT format('CREATE DATABASE %I OWNER %I ENCODING ''UTF8''', :'db', :'app_user') WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'db') \gexec"
    )
    $env:PGPASSWORD = $superPassword
    $env:SHMESH_APP_PASSWORD = $appPassword
    try {
        Invoke-Pg "psql.exe" @("-X", "-q", "-h", "localhost", "-p", "$port", "-U", "postgres", "-d", "postgres",
            "-v", "ON_ERROR_STOP=1", "-v", "app_user=$AppUser", "-v", "db=$Database", "-f", $sql)
    } finally {
        Remove-Item Env:PGPASSWORD, Env:SHMESH_APP_PASSWORD -ErrorAction SilentlyContinue
        Remove-Item $sql -Force -ErrorAction SilentlyContinue
    }

    Write-AdminFile $port $superPassword $appPassword
    if ($ResultFile) { Set-Content -Path $ResultFile -Encoding ASCII -Value @("port=$port", "password=$appPassword") }
    Write-Log "Database setup finished."
    exit 0
}
catch {
    Write-Log "ERROR: $($_.Exception.Message)"
    exit 1
}
