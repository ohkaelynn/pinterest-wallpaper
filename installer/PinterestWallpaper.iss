#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif

[Setup]
AppId={{8F6E1A42-9C3B-4E1B-9F7A-2D6B8E5C1A09}
AppName=Pinterest Wallpaper Swapper
AppVersion={#AppVersion}
AppPublisher=Pinterest Wallpaper Swapper Contributors
AppPublisherURL=https://github.com/ohkaelynn/pinterest-wallpaper
DefaultDirName={localappdata}\Programs\PinterestWallpaper
DefaultGroupName=Pinterest Wallpaper Swapper
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\src\PinterestWallpaper.App\Assets\app.ico
UninstallDisplayIcon={app}\PinterestWallpaper.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
OutputDir=..\dist
OutputBaseFilename=PinterestWallpaper-v{#AppVersion}-win-x64-setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Pinterest Wallpaper Swapper"; Filename: "{app}\PinterestWallpaper.exe"
Name: "{group}\Uninstall Pinterest Wallpaper Swapper"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Pinterest Wallpaper Swapper"; Filename: "{app}\PinterestWallpaper.exe"; Tasks: desktopicon

[Registry]
; Clean up HKCU shell integrations on uninstall
Root: HKCU; Subkey: "Software\Classes\DesktopBackground\Shell\PinterestWallpaper"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PinterestWallpaper"; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\PinterestWallpaper.exe"; Description: "Launch Pinterest Wallpaper Swapper"; Flags: nowait postinstall skipifsilent
