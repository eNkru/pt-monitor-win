# PTMonitor for Windows — build, self-test, package.
#
# PowerShell 5.1+ (the one that ships with Windows 10/11). No admin rights
# needed. Produces, per -Rid (default win-x64):
#
#   publish/<rid>/PTMonitor.exe               self-contained single-file exe
#   publish/<rid>/cli/PTMonitor.Cli.exe       headless selftest / probe CLI
#   artifacts/PTMonitor-<version>-<rid>.zip   the above, zipped
#   artifacts/PTMonitor-<version>-<rid>-setup.exe
#                                             Inno Setup installer (per-user or
#                                             per-machine, chosen in the wizard;
#                                             Windows only, -SkipInstaller or
#                                             -SkipZip skips it)
#   artifacts/SHA256SUMS.txt                  sha256sum -c compatible sums of
#                                             every published artifact in
#                                             artifacts/ (zips + installers;
#                                             rewritten each run, so two -Rid
#                                             runs converge)
#
# Usage:
#   .\scripts\build.ps1                        # full pipeline, win-x64
#   .\scripts\build.ps1 -Rid win-arm64          # full pipeline, ARM64
#   .\scripts\build.ps1 -Version 1.2.3         # stamp a version
#   .\scripts\build.ps1 -SkipZip               # build + selftest + publish only
#   .\scripts\build.ps1 -SkipInstaller         # zip but no setup.exe (needs no
#                                               # Inno Setup on this machine)
#   .\scripts\build.ps1 -SkipGates              # publish only (CI reuses the
#                                               # gates it already ran)

param(
    [string]$Version = "1.0.0",
    [string]$Rid = "win-x64",
    [switch]$SkipGates,
    [switch]$SkipZip,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

# Windows file versions are 16-bit-per-component a.b.c.d, release versions are
# not (1.0.0-dev.<sha7>, 1.0.0-<slug>.<sha7>). Drop the prerelease/build
# suffix, require one to three numeric components, pad to three and append 0:
#   1.0.0                        -> 1.0.0.0
#   2.4                          -> 2.4.0.0
#   1.0.0-dev.abc1234            -> 1.0.0.0
# A version that does not fit throws -- a silently wrong file version is worse
# than a failed build. Single source of truth for every packaged version.
function ConvertTo-NumericVersion {
    param([Parameter(Mandatory = $true)][string]$Version)

    $core = $Version.Split([char[]]@('-', '+'), 2)[0]
    $parts = @($core.Split('.'))
    if ($parts.Count -gt 3) {
        throw "version '$Version' has $($parts.Count) numeric components; Windows file versions allow at most 3 (plus the build component)"
    }
    foreach ($part in $parts) {
        if ($part -notmatch '^\d{1,5}$') {
            throw "version '$Version' has a non-numeric component '$part'"
        }
    }
    while ($parts.Count -lt 3) { $parts += '0' }
    return ($parts -join '.') + '.0'
}

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
# Packaging options are evidence-driven, not habit: a single self-extracting exe
# (Avalonia needs IncludeNativeLibrariesForSelfExtract). No PublishReadyToRun --
# measured on Linux 2026-11-05 with no startup gain (105 vs 109 ms), and on
# Windows 2026-11-05 the launch timeline showed entry->log-ready = 12 ms (the
# bundle/extraction cost) and total = 575 ms against a 1500 ms target, so the
# bundle is not the bottleneck. Only add ReadyToRun after a Windows A/B of the
# same build shows a gain. Keep this identical to build.sh.
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

    # The installer packages the same publish/<rid> output. -SkipZip skips it
    # too: an installer without the zip beside it is a debugging mode nobody
    # wants, and the sums below are written by the zip step.
    if (-not $SkipInstaller) {
        Write-Host "==> installer (Inno Setup)" -ForegroundColor Cyan
        $numericVersion = ConvertTo-NumericVersion -Version $Version

        # iscc discovery: Get-Command first (the CI runner image ships a
        # Chocolatey *shim* named iscc on PATH), then the default install
        # locations. A shim is a tiny launcher with no version resource of its
        # own -- its FileVersion reads 0.0.0.0 -- so the version comes from the
        # real ISCC.exe it launches: named in the shim's .shim file, or one of
        # the install directories. 6.3 is the floor: the `x64compatible`
        # architecture token and the modern wizard used by PTMonitor.iss do not
        # exist before it. A *missing* iscc fails the build -- never a silent
        # skip. An *unreadable* version only warns: a launcher in front of a
        # good compiler is legitimate, and the compile below is the real check.
        $isccCandidates = @()
        $isccCommand = Get-Command iscc -ErrorAction SilentlyContinue
        if ($isccCommand) { $isccCandidates += $isccCommand.Source }
        $isccCandidates += @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
        )
        $isccCandidates = @($isccCandidates | Where-Object { $_ -and (Test-Path $_) } |
            Select-Object -Unique)
        if (-not $isccCandidates) {
            throw "Inno Setup 6 (iscc) was not found on PATH, in ${env:ProgramFiles(x86)}\Inno Setup 6 or in ${env:ProgramFiles}\Inno Setup 6. Install it from https://jrsoftware.org/isdl.php, or pass -SkipInstaller to build only the zip."
        }

        # Resolve to whatever reports a real version: the candidate itself, or
        # the target a Chocolatey shim names in `<candidate>.shim` (XML).
        $iscc = $null
        $isccVersion = $null
        foreach ($candidate in $isccCandidates) {
            $version = [string](Get-Item $candidate).VersionInfo.FileVersion
            if ($version -match '^(\d+)\.(\d+)' -and [int]$Matches[1] -gt 0) {
                $iscc = $candidate
                $isccVersion = $version
                break
            }
            $shim = "$candidate.shim"
            if (-not (Test-Path $shim)) { continue }
            $shimText = Get-Content -Raw $shim
            if ($shimText -notmatch '<(?:path|target)>([^<]+)</(?:path|target)>') { continue }
            $target = $Matches[1]
            if (-not (Test-Path $target)) { continue }
            $iscc = $target
            $isccVersion = [string](Get-Item $target).VersionInfo.FileVersion
            break
        }
        if (-not $iscc) {
            # No version anywhere (a shim in front of an unreadable target): use
            # the first candidate and let the compile speak for itself.
            $iscc = $isccCandidates[0]
            $isccVersion = $null
        }
        if ($isccVersion -notmatch '^(\d+)\.(\d+)') {
            Write-Host "==> warning: the Inno Setup version at $iscc could not be read; assuming 6.3 or newer" -ForegroundColor Yellow
        } else {
            $isccMajor = [int]$Matches[1]
            $isccMinor = [int]$Matches[2]
            if ($isccMajor -lt 6 -or ($isccMajor -eq 6 -and $isccMinor -lt 3)) {
                throw "Inno Setup $isccVersion at $iscc is too old; 6.3 or newer is required (https://jrsoftware.org/isdl.php)"
            }
            Write-Host "==> iscc $isccVersion ($iscc)"
        }

        & $iscc /Qp "/DAppVersion=$Version" "/DAppVersionNumeric=$numericVersion" "/DRid=$Rid" "scripts/installer/PTMonitor.iss"
        if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
        Write-Host "==> built artifacts/PTMonitor-$Version-$Rid-setup.exe"
    }

    # sha256sum format: "<64 lowercase hex><two spaces><basename>", so
    # `sha256sum -c SHA256SUMS.txt` works on Linux/macOS and a Get-FileHash
    # comparison works on Windows. Rewritten from EVERY published artifact in
    # artifacts/ (zips and setup exes, sorted by name), not appended, so running
    # the script once per -Rid converges on the set instead of piling up
    # duplicate lines. Listing the installer too is what keeps a later
    # build.sh run (which builds no installer) from dropping its checksum
    # line. ASCII, no BOM: a BOM would make the first line unparseable for
    # `sha256sum -c`.
    # CI recomputes the same sums from the uploaded bytes — this copy is what
    # lets a self-built binary be compared with a released one.
    $sums = Get-ChildItem artifacts -File |
        Where-Object { $_.Name -like "PTMonitor-*.zip" -or $_.Name -like "PTMonitor-*-setup.exe" } |
        Sort-Object Name |
        ForEach-Object {
            $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $($_.Name)"
        }
    Set-Content -Path "artifacts/SHA256SUMS.txt" -Value $sums -Encoding ascii
    Write-Host "==> built artifacts/SHA256SUMS.txt"
}

Write-Host "==> done. Run: publish\$Rid\PTMonitor.exe" -ForegroundColor Green
