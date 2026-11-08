; PTMonitor installer -- Inno Setup 6, one script for both architectures and
; both install modes.
;
; Built by scripts/build.ps1 (and by the CI package jobs) with:
;
;   iscc /Qp /DPtmVersion=<display version> /DPtmVersionNumeric=<x.y.z.w> \
;        /DPtmRid=<win-x64|win-arm64> scripts\installer\PTMonitor.iss
;
; It packages publish\<PtmRid>\PTMonitor.exe and publish\<PtmRid>\cli\PTMonitor.Cli.exe
; into artifacts\PTMonitor-<version>-<rid>-setup.exe.
;
; Install mode is chosen in the wizard:
;   * per-user (default, PrivilegesRequired=lowest) -> no UAC, files land in
;     %LOCALAPPDATA%\Programs\PTMonitor, Start Menu entry in the user's profile;
;   * per-machine (PrivilegesRequiredOverridesAllowed=dialog) -> one UAC prompt,
;     %ProgramFiles%\PTMonitor, Start Menu entry visible to every user.
; /CURRENTUSER and /ALLUSERS do the same for silent installs.
;
; UsePreviousPrivileges=yes makes an upgrade reuse the detected mode and skip
; the mode page, so a newer build replaces the existing install in place (one
; Installed-apps entry) instead of adding a second copy in the other location.
;
; AppId is IMMUTABLE: Inno matches upgrades and the uninstall key on this GUID.
; Changing it makes every future build install side by side with the old one.
;
; This file is ASCII only, with no BOM: Inno reads a BOM-less script as the
; ANSI code page, so a stray non-ASCII byte would corrupt a message. Keep it
; ASCII when editing.

#define Root AddBackslash(SourcePath) + "..\..\"

; The three values below come from the build script. Fail with a readable
; message instead of producing PTMonitor--win--setup.exe from an empty define.
; The names are deliberately prefixed: an ISPP define called `AppVersion` came
; back as 0.0.0.0 in a real compile, apparently shadowed by the [Setup]
; directive / compiler symbol of the same name. Keep the `Ptm` prefix -- a
; plain `Rid`/`Version` symbol is asking for the same collision.
#ifndef PtmVersion
#error PtmVersion is not defined. Use scripts\build.ps1, or pass /DPtmVersion=<version> to iscc.
#endif
#ifndef PtmVersionNumeric
#error PtmVersionNumeric is not defined. Use scripts\build.ps1, or pass /DPtmVersionNumeric=<x.y.z.w> to iscc.
#endif
#ifndef PtmRid
#error PtmRid is not defined. Use scripts\build.ps1, or pass /DPtmRid=win-x64 or /DPtmRid=win-arm64 to iscc.
#endif

#if PtmRid == "win-x64"
  ; x64compatible also runs on Arm64 Windows through x64 emulation, which is
  ; what the portable win-x64 exe does today.
  #define Arch "x64compatible"
#elif PtmRid == "win-arm64"
  #define Arch "arm64"
#else
#error Unsupported PtmRid. Use win-x64 or win-arm64.
#endif

[Setup]
AppId={{41E97050-0E4C-4F6E-B0F2-00318AD6F145}
AppName=PTMonitor
; The exact release string (1.0.0, 1.0.0-dev.<sha7>, ...), so a dev build is
; identifiable in Installed apps.
AppVersion={#PtmVersion}
AppVerName=PTMonitor {#PtmVersion}
AppPublisher=eNkru
AppPublisherURL=https://github.com/eNkru/pt-monitor-win
AppSupportURL=https://github.com/eNkru/pt-monitor-win
AppUpdatesURL=https://github.com/eNkru/pt-monitor-win
; Resolves to %ProgramFiles%\PTMonitor per-machine and
; %LOCALAPPDATA%\Programs\PTMonitor per-user.
DefaultDirName={autopf}\PTMonitor
DefaultGroupName=PTMonitor
DisableProgramGroupPage=yes
; Default is per-user with no UAC; the mode page (and /CURRENTUSER /ALLUSERS)
; offers the per-machine install.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
; Explicit: on an upgrade reuse the detected mode and do not ask again.
UsePreviousPrivileges=yes
ArchitecturesAllowed={#Arch}
ArchitecturesInstallIn64BitMode={#Arch}
MinVersion=10.0.14393
OutputDir={#Root}artifacts
OutputBaseFilename=PTMonitor-{#PtmVersion}-{#PtmRid}-setup
SetupIconFile={#Root}src\PTMonitor.App\app.ico
UninstallDisplayIcon={app}\PTMonitor.exe
; VersionInfo* must be numeric a.b.c.d; VersionInfoVersion is the file version,
; VersionInfoProductVersion the product version. build.ps1 derives both from
; the display version.
VersionInfoVersion={#PtmVersionNumeric}
VersionInfoProductVersion={#PtmVersionNumeric}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; An upgrade can run while the tray app is running: let Setup close it, but do
; not silently relaunch it afterwards.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#Root}publish\{#PtmRid}\PTMonitor.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Root}publish\{#PtmRid}\cli\PTMonitor.Cli.exe"; DestDir: "{app}\cli"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\PTMonitor"; Filename: "{app}\PTMonitor.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\PTMonitor.exe"; Description: "Launch PTMonitor"; Flags: nowait postinstall skipifsilent
