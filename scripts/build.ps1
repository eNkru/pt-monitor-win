# PTMonitor for Windows — build, self-test, package.
#
# PowerShell 5.1+ (the one that ships with Windows 10/11). No admin rights
# needed. Produces, per -Rid (default win-x64):
#
#   publish/<rid>/PTMonitor.exe               self-contained single-file exe
#   publish/<rid>/cli/PTMonitor.Cli.exe       headless selftest / probe CLI
#   artifacts/PTMonitor-<version>-<rid>.zip   the above, zipped
#
# Usage:
#   .\scripts\build.ps1                        # full pipeline, win-x64
#   .\scripts\build.ps1 -Rid win-arm64          # full pipeline, ARM64
#   .\scripts\build.ps1 -Version 1.2.3         # stamp a version
#   .\scripts\build.ps1 -SkipZip               # build + selftest + publish only
#   .\scripts\build.ps1 -SkipGates              # publish only (CI reuses the
#                                               # gates it already ran)

param(
    [string]$Version = "1.0.0",
    [string]$Rid = "win-x64",
    [switch]$SkipGates,
    [switch]$SkipZip
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

if (-not $SkipGates) {
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
}

Write-Host "==> dotnet publish $Rid (self-contained)" -ForegroundColor Cyan
dotnet publish src/PTMonitor.App -c Release -r $Rid `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version `
    -o "publish/$Rid"
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "==> publish the packaged CLI ($Rid)" -ForegroundColor Cyan
dotnet publish src/PTMonitor.Cli -c Release -r $Rid `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:Version=$Version `
    -o "publish/$Rid/cli"
if ($LASTEXITCODE -ne 0) { throw "cli publish failed" }

# The packaged selftest only runs when the exe matches this machine's
# architecture: a cross-compiled win-arm64 binary cannot execute on an x64
# runner, and neither runs on a Linux/macOS development box. The pre-publish
# --selftest above is architecture-independent, so the gate is never skipped.
$hostRid = if ($env:OS -eq "Windows_NT") {
    if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "win-arm64" } else { "win-x64" }
} else {
    $null
}

if ($Rid -eq $hostRid) {
    Write-Host "==> self-test the packaged CLI" -ForegroundColor Cyan
    & "publish/$Rid/cli/PTMonitor.Cli.exe" --selftest
    if ($LASTEXITCODE -ne 0) { throw "packaged selftest failed" }
} else {
    Write-Host "==> skipping packaged selftest ($Rid does not run on this host)" -ForegroundColor Yellow
}

if (-not $SkipZip) {
    Write-Host "==> zip" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path artifacts | Out-Null
    $zip = "artifacts/PTMonitor-$Version-$Rid.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path "publish/$Rid" -DestinationPath $zip
    Write-Host "==> built $zip"
}

Write-Host "==> done. Run: publish\$Rid\PTMonitor.exe" -ForegroundColor Green
