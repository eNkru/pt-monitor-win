# PTMonitor for Windows — build, self-test, package.
#
# PowerShell 5.1+ (the one that ships with Windows 10/11). No admin rights
# needed. Produces:
#
#   publish/win-x64/PTMonitor.exe          self-contained single-file exe
#   artifacts/PTMonitor-win-x64.zip        the above, zipped
#
# Usage:
#   .\scripts\build.ps1                 # full pipeline
#   .\scripts\build.ps1 -SkipZip        # build + selftest + publish only

param(
    [string]$Version = "1.0.0",
    [switch]$SkipZip
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

Write-Host "==> dotnet restore" -ForegroundColor Cyan
dotnet restore
if ($LASTEXITCODE -ne 0) { throw "restore failed" }

Write-Host "==> dotnet build (Release)" -ForegroundColor Cyan
dotnet build -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host "==> self-test (built-in checks)" -ForegroundColor Cyan
dotnet run --project src/PTMonitor.Cli -c Release --no-build -- --selftest
if ($LASTEXITCODE -ne 0) { throw "selftest failed" }

Write-Host "==> dotnet test" -ForegroundColor Cyan
dotnet test tests/PTMonitor.Core.Tests -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw "tests failed" }

Write-Host "==> dotnet publish (self-contained win-x64)" -ForegroundColor Cyan
dotnet publish src/PTMonitor.App -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version `
    -o publish/win-x64
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "==> self-test the packaged CLI" -ForegroundColor Cyan
dotnet publish src/PTMonitor.Cli -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:Version=$Version `
    -o publish/win-x64/cli
if ($LASTEXITCODE -ne 0) { throw "cli publish failed" }
& publish/win-x64/cli/PTMonitor.Cli.exe --selftest
if ($LASTEXITCODE -ne 0) { throw "packaged selftest failed" }

if (-not $SkipZip) {
    Write-Host "==> zip" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path artifacts | Out-Null
    $zip = "artifacts/PTMonitor-$Version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path publish/win-x64 -DestinationPath $zip
    Write-Host "==> built $zip"
}

Write-Host "==> done. Run: publish\win-x64\PTMonitor.exe" -ForegroundColor Green
