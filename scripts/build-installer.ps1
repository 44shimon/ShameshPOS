<#
.SYNOPSIS
    Publishes Shmesh POS (tests first) and compiles the Windows installer
    publish\installer\ShmeshPOS-Setup-<version>.exe. The version comes from src\ShmeshPOS\ShmeshPOS.csproj.

.DESCRIPTION
    Needs Inno Setup 6 (https://jrsoftware.org/isinfo.php), e.g.:
        winget install --id JRSoftware.InnoSetup -e --source winget --scope user

.EXAMPLE
    .\scripts\build-installer.ps1
    .\scripts\build-installer.ps1 -SkipTests
#>
param(
    [switch]$SkipTests,
    [string]$Iscc
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

if ($SkipTests) {
    Write-Host "Publishing (tests skipped)..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot "publish.ps1") -Output $app -SkipTests
} else {
    & (Join-Path $PSScriptRoot "publish.ps1") -Output $app
}

Write-Host "Compiling installer $version ..." -ForegroundColor Cyan
& $Iscc /Q "/DAppVersion=$version" "/DSourceDir=$app" (Join-Path $root "installer\ShmeshPOS.iss")
if ($LASTEXITCODE -ne 0) { throw "Installer compile failed." }

$setup = Join-Path $root "publish\installer\ShmeshPOS-Setup-$version.exe"
$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host ""
Write-Host "Done: $setup ($size MB)" -ForegroundColor Green
Write-Host "Run it on each register as an administrator. The PostgreSQL server must already be set up (README: Installing on a register)."
