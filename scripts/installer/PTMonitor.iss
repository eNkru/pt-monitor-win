; PTMonitor installer -- Inno Setup 6, one script for both architectures and
; both install modes.
;
; Built by scripts/build.ps1 (and by the CI package jobs) with:
;
;   iscc /Qp /DAppVersion=<display version> /DAppVersionNumeric=<x.y.z.w> \
;        /DRid=<win-x64|win-arm64> scripts\installer\PTMonitor.iss
;
; It packages publish\<Rid>\PTMonitor.exe and publish\<Rid>\cli\PTMonitor.Cli.exe
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
#ifndef AppVersion
  #error AppVersion is not defined. Use scripts\build.ps1, or pass /DAppVersion=<version> to iscc.
#endif
#ifndef AppVersionNumeric
  #error AppVersionNumeric is not defined. Use scripts\build.ps1, or pass /DAppVersionNumeric=<x.y.z.w> to iscc.
#endif
#ifndef Rid
  #error Rid is not defined. Use scripts\build.ps1, or pass /DRid=win-x64 or /DRid=win-arm64 to iscc.
#endif

#if Rid == "win-x64"
  ; x64compatible also runs on Arm64 Windows through x64 emulation, which is
  ; what the portable win-x64 exe does today.
  #define Arch "x64compatible"
#elif Rid == "win-arm64"
  #define Arch "arm64"
#else
  #error Unsupported Rid. Use win-x64 or win-arm64.
#endif

[Setup]
AppId={{41E97050-0E4C-4F6E-B0F2-00318AD6F145}
AppName=PTMonitor
; The exact release string (1.0.0, 1.0.0-dev.<sha7>, ...), so a dev build is
; identifiable in Installed apps.
AppVersion={#AppVersion}
AppVerName=PTMonitor {#AppVersion}
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
OutputBaseFilename=PTMonitor-{#AppVersion}-{#Rid}-setup
SetupIconFile={#Root}src\PTMonitor.App\app.ico
UninstallDisplayIcon={app}\PTMonitor.exe
; VersionInfo* must be numeric a.b.c.d; VersionInfoVersion is the file version,
; VersionInfoProductVersion the product version. build.ps1 derives both from
; the display version.
VersionInfoVersion={#AppVersionNumeric}
VersionInfoProductVersion={#AppVersionNumeric}
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
Source: "{#Root}publish\{#Rid}\PTMonitor.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Root}publish\{#Rid}\cli\PTMonitor.Cli.exe"; DestDir: "{app}\cli"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\PTMonitor"; Filename: "{app}\PTMonitor.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\PTMonitor.exe"; Description: "Launch PTMonitor"; Flags: nowait postinstall skipifsilent
