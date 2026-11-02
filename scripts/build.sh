#!/usr/bin/env bash
# Build Windows packages from Linux/WSL using the Linux .NET SDK (no PowerShell).
# Keep gate order, publish options and package layout in sync with build.ps1.
set -euo pipefail

usage() {
    printf '%s\n' \
        'Usage: ./scripts/build.sh [options]' \
        '  --rid win-x64|win-arm64   Windows target (default: win-x64)' \
        '  --version VERSION        Package version (default: 1.0.0)' \
        '  --skip-gates             Skip build/selftest/unit tests (already verified only)' \
        '  --skip-zip               Publish app and CLI without ZIP/checksums' \
        '  --invariant              Use invariant globalization for Linux tests only' \
        '  -h, --help               Show this help' \
        '' \
        'Requires Bash, Python 3, and the Linux .NET 8 SDK on PATH or in ~/.dotnet.' \
        'Missing ICU is detected automatically; Windows packages always retain' \
        'normal globalization. Temporary build outputs are isolated from bin/obj.' \
        'Existing generated publish/<rid> output is replaced after successful builds.' \
        '' \
        'Outputs: publish/<rid>/PTMonitor.exe, publish/<rid>/cli/PTMonitor.Cli.exe,' \
        '         artifacts/PTMonitor-<version>-<rid>.zip, artifacts/SHA256SUMS.txt' \
        'Run the published EXE on Windows, preferably copied to a local C: folder.'
}
fail() { printf 'Error: %s\n' "$*" >&2; exit 1; }
step() { printf '\n==> %s\n' "$*"; }

rid=win-x64
version=1.0.0
skip_gates=false
skip_zip=false
invariant=false
while (($#)); do
    case "$1" in
        --rid|--version)
            (($# >= 2)) && [[ -n "$2" && "$2" != --* ]] || fail "$1 requires a value"
            if [[ "$1" == --rid ]]; then rid=$2; else version=$2; fi
            shift 2 ;;
        --skip-gates) skip_gates=true; shift ;;
        --skip-zip) skip_zip=true; shift ;;
        --invariant) invariant=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *) fail "Unknown option: $1 (use --help)" ;;
    esac
done
[[ "$rid" == win-x64 || "$rid" == win-arm64 ]] || fail 'RID must be win-x64 or win-arm64'
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$ ]] ||
    fail 'Version must look like 1.0.0 or 1.0.0-preview.1'
[[ "$(uname -s)" == Linux ]] || fail 'Run this script in Linux/WSL; use build.ps1 on Windows'
command -v python3 >/dev/null || fail 'Python 3 is required: sudo apt install python3'

root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"
# Do not accidentally select a Windows dotnet.exe exposed through WSL interop.
if command -v dotnet >/dev/null; then
    dotnet_bin=$(command -v dotnet)
elif [[ -x "${DOTNET_ROOT:-$HOME/.dotnet}/dotnet" ]]; then
    dotnet_bin="${DOTNET_ROOT:-$HOME/.dotnet}/dotnet"
else
    fail 'Linux .NET 8 SDK not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0'
fi
export PATH="$(dirname -- "$dotnet_bin"):$PATH"

# The SDK itself may need invariant mode to start on minimal Debian installs.
# This changes only local test builds, NEVER the shipped Windows runtimeconfig.
if [[ "${DOTNET_SYSTEM_GLOBALIZATION_INVARIANT:-0}" == 1 ||
      "${DOTNET_SYSTEM_GLOBALIZATION_INVARIANT:-0}" == true ]]; then
    invariant=true
elif ! python3 - <<'PY'
import ctypes
import ctypes.util
import sys
try:
    library = ctypes.util.find_library('icuuc')
    if not library:
        sys.exit(1)
    ctypes.CDLL(library)
except OSError:
    sys.exit(1)
PY
then
    invariant=true
fi
if [[ "$invariant" == true ]]; then
    export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
    step 'Linux tools/tests use invariant globalization; Windows output will NOT'
fi
sdks=$("$dotnet_bin" --list-sdks) || fail "Cannot run Linux SDK at $dotnet_bin"
grep -q '^8\.0\.' <<< "$sdks" || fail "Linux .NET 8 SDK required; found: ${sdks:-none}"
step "Using $dotnet_bin; target $rid, version $version"

work=$(mktemp -d "${TMPDIR:-/tmp}/ptmonitor-build.XXXXXX")
trap 'rm -rf -- "$work"' EXIT
host_options=(--artifacts-path "$work/host" "-p:InvariantGlobalization=$invariant")
if [[ "$skip_gates" == false ]]; then
    step 'dotnet restore'
    "$dotnet_bin" restore PTMonitor.sln "${host_options[@]}"
    step 'dotnet build (Release)'
    "$dotnet_bin" build PTMonitor.sln -c Release --no-restore "${host_options[@]}"
    step 'self-test (built-in checks)'
    "$dotnet_bin" "$work/host/bin/PTMonitor.Cli/release/PTMonitor.Cli.dll" --selftest
    step 'dotnet test'
    "$dotnet_bin" test tests/PTMonitor.Core.Tests -c Release --no-build --no-restore "${host_options[@]}"
else
    step 'WARNING: gates skipped by request; this run does not verify the source'
fi

# Stage both outputs together so a failed CLI publish cannot replace a good app.
# Separate Windows intermediates prevent local invariant test outputs leaking in.
out="$work/output/$rid"
publish_options=(-c Release -r "$rid" --self-contained true
    -p:PublishSingleFile=true -p:InvariantGlobalization=false "-p:Version=$version"
    --artifacts-path "$work/windows")
step "dotnet publish app ($rid, self-contained)"
"$dotnet_bin" publish src/PTMonitor.App "${publish_options[@]}" \
    -p:IncludeNativeLibrariesForSelfExtract=true -o "$out"
step "dotnet publish CLI ($rid, self-contained)"
"$dotnet_bin" publish src/PTMonitor.Cli "${publish_options[@]}" -o "$out/cli"
[[ -f "$out/PTMonitor.exe" && -f "$out/cli/PTMonitor.Cli.exe" ]] || fail 'Publish did not produce both Windows executables'
step 'Packaged Windows CLI is not executed on Linux; host selftest is the gate'
mkdir -p publish
rm -rf -- "publish/$rid"
mv -- "$out" "publish/$rid"

if [[ "$skip_zip" == false ]]; then
    step 'ZIP and SHA256SUMS.txt'
    # Python is available on Debian; no separate zip utility is needed. Match
    # build.ps1: one RID directory at the ZIP root, all PTMonitor ZIPs in sums.
    python3 - "$rid" "$version" <<'PY'
import hashlib
from pathlib import Path
import sys
import zipfile

rid, version = sys.argv[1:]
publish = Path('publish')
artifacts = Path('artifacts')
artifacts.mkdir(exist_ok=True)
archive = artifacts / f'PTMonitor-{version}-{rid}.zip'
temporary = archive.with_suffix('.zip.tmp')
try:
    with zipfile.ZipFile(temporary, 'w', zipfile.ZIP_DEFLATED) as output:
        for file in sorted((publish / rid).rglob('*')):
            if file.is_file():
                output.write(file, file.relative_to(publish).as_posix())
    temporary.replace(archive)
finally:
    temporary.unlink(missing_ok=True)

lines = []
for file in sorted(artifacts.glob('PTMonitor-*.zip')):
    digest = hashlib.sha256()
    with file.open('rb') as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b''):
            digest.update(chunk)
    lines.append(f'{digest.hexdigest()}  {file.name}\n')
sums = artifacts / 'SHA256SUMS.txt'
temporary_sums = sums.with_suffix('.txt.tmp')
try:
    temporary_sums.write_text(''.join(lines), encoding='ascii')
    temporary_sums.replace(sums)
finally:
    temporary_sums.unlink(missing_ok=True)
print(f'Built {archive}\nBuilt {sums}')
PY
fi
step "Done. Windows executable: $root/publish/$rid/PTMonitor.exe"
