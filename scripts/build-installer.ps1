<#
.SYNOPSIS
    Publishes Shmesh POS (tests first) and compiles the Windows installer
    publish\installer\ShmeshPOS-Setup-<version>.exe. The version comes from src\ShmeshPOS\ShmeshPOS.csproj.

.DESCRIPTION
    Needs Inno Setup 6 (https://jrsoftware.org/isinfo.php), e.g.:
        winget install --id JRSoftware.InnoSetup -e --source winget --scope user

    The installer bundles everything a clean computer needs: the PostgreSQL server and tools and the Visual C++
    runtime. They are downloaded once into publish\deps (about 360 MB) and checked against pinned SHA-256 hashes.

.EXAMPLE
    .\scripts\build-installer.ps1
    .\scripts\build-installer.ps1 -SkipTests
#>
param(
    [switch]$SkipTests,
    [string]$Iscc,
    [string]$PgVersion = "18.4-1"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$app = Join-Path $root "publish\ShmeshPOS"

# Version from the project file, so the installer, exe and Programs list always agree.
[xml]$csproj = Get-Content (Join-Path $root "src\ShmeshPOS\ShmeshPOS.csproj")
$version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw "No <Version> found in ShmeshPOS.csproj." }

if (-not $Iscc) {
    $Iscc = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $Iscc) {
    throw "Inno Setup 6 was not found. Install it: winget install --id JRSoftware.InnoSetup -e --source winget --scope user"
}

# Dependencies bundled for a clean computer: the PostgreSQL server (EDB binaries, without pgAdmin/StackBuilder)
# and the Visual C++ runtime it needs. Downloaded once into publish\deps and checked against pinned hashes.
$deps = Join-Path $root "publish\deps"
$downloads = @(
    @{ File = "postgresql-$PgVersion-windows-x64-binaries.zip"
       Url = "https://get.enterprisedb.com/postgresql/postgresql-$PgVersion-windows-x64-binaries.zip"
       Sha256 = "7effe34c0bf89027b3f171447d351cbc460f4566c8d0f643daec67f140787858" },
    @{ File = "vc_redist.x64.exe"
       Url = "https://aka.ms/vs/17/release/vc_redist.x64.exe"
       Sha256 = "cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b" }
)
New-Item -ItemType Directory -Force $deps | Out-Null
foreach ($d in $downloads) {
    $path = Join-Path $deps $d.File
    if (-not (Test-Path $path)) {
        Write-Host "Downloading $($d.File)..." -ForegroundColor Cyan
        Invoke-WebRequest -Uri $d.Url -OutFile "$path.partial" -UseBasicParsing
        Move-Item "$path.partial" $path
    }
    $hash = (Get-FileHash $path -Algorithm SHA256).Hash
    if ($hash -ne $d.Sha256) {
        Remove-Item $path
        throw "$($d.File) has an unexpected SHA-256 ($hash) and was deleted. If the download changed on purpose, update the hash in build-installer.ps1."
    }
}

$pgsql = Join-Path $root "publish\pgsql"
if (-not (Test-Path (Join-Path $pgsql "bin\postgres.exe"))) {
    Write-Host "Extracting PostgreSQL $PgVersion..." -ForegroundColor Cyan
    if (Test-Path $pgsql) { Remove-Item -Recurse -Force $pgsql }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $deps $downloads[0].File))
    try {
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName -replace "^pgsql/", ""
            if ($name -notmatch "^(bin|lib|share)/" -and $name -ne "server_license.txt") { continue }
            if ($name.EndsWith("/") -or $name -match "\.(pdb|lib)$" -or $name -match "^bin/(stackbuilder\.exe|wx.*\.dll)$") { continue }
            $target = Join-Path $pgsql $name
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    } finally { $zip.Dispose() }
}

if ($SkipTests) {
    Write-Host "Publishing (tests skipped)..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot "publish.ps1") -Output $app -SkipTests
} else {
    & (Join-Path $PSScriptRoot "publish.ps1") -Output $app
}

Write-Host "Compiling installer $version ..." -ForegroundColor Cyan
& $Iscc /Q "/DAppVersion=$version" "/DSourceDir=$app" "/DPgSqlDir=$pgsql" "/DDepsDir=$deps" `
    "/DPgVersion=$($PgVersion -replace '-\d+$', '')" (Join-Path $root "installer\ShmeshPOS.iss")
if ($LASTEXITCODE -ne 0) { throw "Installer compile failed." }

$setup = Join-Path $root "publish\installer\ShmeshPOS-Setup-$version.exe"
$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host ""
Write-Host "Done: $setup ($size MB)" -ForegroundColor Green
Write-Host "Run it as an administrator: on the database server (or a single register) choose 'This computer is the database server'."
