<#
.SYNOPSIS
    Opens the built installer in Windows Sandbox, a clean throw-away Windows, to test a fresh install.

.DESCRIPTION
    Writes publish\test-in-sandbox.wsb (Sandbox needs absolute paths, so it is generated per computer) and starts
    it. The Sandbox shows publish\installer on its desktop, read-only. Everything inside is discarded when its
    window closes.

    Build the installer first with .\scripts\build-installer.ps1. Windows Sandbox needs Windows 10/11 Pro or
    Enterprise with virtualization on; turn it on once (as administrator, then restart):
        Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All

.EXAMPLE
    .\scripts\test-in-sandbox.ps1
    .\scripts\test-in-sandbox.ps1 -MemoryMB 6144
#>
param(
    [int]$MemoryMB = 4096
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$installers = Join-Path $root "publish\installer"

$setup = Get-ChildItem $installers -Filter "ShmeshPOS-Setup-*.exe" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $setup) { throw "No installer in $installers. Build it first: .\scripts\build-installer.ps1" }

$sandbox = Join-Path $env:SystemRoot "System32\WindowsSandbox.exe"
if (-not (Test-Path $sandbox)) {
    throw "Windows Sandbox is not turned on. In PowerShell as administrator run:`n" +
          "    Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All`n" +
          "then restart Windows."
}

$desktop = "C:\Users\WDAGUtilityAccount\Desktop\installer"
$wsb = Join-Path $root "publish\test-in-sandbox.wsb"
Set-Content -Path $wsb -Encoding UTF8 -Value @"
<Configuration>
  <MemoryInMB>$MemoryMB</MemoryInMB>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$installers</HostFolder>
      <SandboxFolder>$desktop</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>explorer.exe $desktop</Command>
  </LogonCommand>
</Configuration>
"@

Write-Host "Opening Windows Sandbox with $($setup.Name) ($([math]::Round($setup.Length / 1MB, 1)) MB)..." -ForegroundColor Cyan
Write-Host "In the Sandbox: run the installer, choose 'Database server', then start Shmesh POS and log in."
Write-Host "Setup log inside the Sandbox: C:\ProgramData\ShmeshPOS\logs\setup-database.log. Closing the window discards everything."
Start-Process $wsb
