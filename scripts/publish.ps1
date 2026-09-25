<#
.SYNOPSIS
    Builds a self-contained Shmesh POS folder to copy to a register (no .NET install needed on the register).

.EXAMPLE
    .\scripts\publish.ps1
    .\scripts\publish.ps1 -Output D:\ShmeshPOS-release
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot "..\publish\ShmeshPOS"),
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $root "src\ShmeshPOS\ShmeshPOS.csproj"

Write-Host "Running tests..." -ForegroundColor Cyan
dotnet test (Join-Path $root "ShmeshPOS.sln") --configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "Tests failed; nothing was published." }

if (Test-Path $Output) { Remove-Item -Recurse -Force $Output }

Write-Host "Publishing to $Output ..." -ForegroundColor Cyan
dotnet publish $project `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=embedded `
    --output $Output
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

Copy-Item (Join-Path $PSScriptRoot "register-appsettings.example.json") $Output
Copy-Item (Join-Path $root "README.md") $Output

$exe = Join-Path $Output "ShmeshPOS.exe"
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Done: $exe ($size MB)" -ForegroundColor Green
Write-Host "Copy the whole folder to the register (e.g. C:\Program Files\ShmeshPOS), then follow 'Installing on a register' in README.md."
