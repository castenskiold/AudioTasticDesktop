; P10: the Windows installer (Inno Setup 6.3+ or 7). Built in CI from a published folder:
;   ISCC /DAppVersion=0.10.1 /DSourceDir=..\..\publish\win-x64 /DRid=win-x64 /O..\..\packages packaging\windows\AudioTastic.iss
; Installs for the current user (no administrator rights), adds the Start menu shortcut with AudioTastic's
; AppUserModelID (media controls and notifications name the app through it, SI-02/SI-07) and an uninstall entry.
; File types and the Explorer context menu are set up by AudioTastic itself (first-start question, Settings ›
; System integration); the uninstaller runs "AudioTastic --unregister" to remove them. Settings, playlists and
; user plugins in %APPDATA%\AudioTastic are kept.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Rid
  #define Rid "win-x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\publish\" + Rid
#endif

[Setup]
; Never change AppId: it identifies the installation for upgrades and uninstalling.
AppId={{4429A0ED-AE1B-4043-B30E-D9CA874A72B5}
AppName=AudioTastic
AppVersion={#AppVersion}
AppVerName=AudioTastic {#AppVersion}
AppPublisher=Big Apps ApS
AppPublisherURL=https://github.com/castenskiold/AudioTasticDesktop
AppSupportURL=https://github.com/castenskiold/AudioTasticDesktop/issues
AppUpdatesURL=https://github.com/castenskiold/AudioTasticDesktop/releases
AppCopyright=Copyright (c) Big Apps ApS
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\AudioTastic
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
LicenseFile=..\..\LICENSE
SetupIconFile=..\..\assets\branding\audiotastic.ico
UninstallDisplayIcon={app}\AudioTastic.exe
UninstallDisplayName=AudioTastic
OutputBaseFilename=AudioTastic-{#AppVersion}-{#Rid}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no
#if Rid == "win-arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Files of an earlier version that this one no longer ships (built-in plugins are replaced as a whole).
Type: filesandordirs; Name: "{app}\plugins"

[Icons]
; The same path and AppUserModelID as the shortcut AudioTastic keeps itself, so the app sees it as its own.
Name: "{autoprograms}\AudioTastic"; Filename: "{app}\AudioTastic.exe"; WorkingDir: "{app}"; Comment: "Winamp-style audio player"; AppUserModelID: "BigApps.AudioTastic"
Name: "{autodesktop}\AudioTastic"; Filename: "{app}\AudioTastic.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{autoprograms}\AudioTastic User Manual"; Filename: "{app}\AudioTastic User Manual.pdf"; Comment: "How to use AudioTastic"

[Run]
Filename: "{app}\AudioTastic.exe"; Description: "{cm:LaunchProgram,AudioTastic}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Removes the file types, Explorer context menu entries, the app's own Start menu shortcut and its app identity.
Filename: "{app}\AudioTastic.exe"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "AudioTasticUnregister"
