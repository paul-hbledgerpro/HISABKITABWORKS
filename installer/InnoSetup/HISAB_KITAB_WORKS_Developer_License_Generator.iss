#define MyAppName "HISAB KITAB WORKS License Generator"
#ifndef MyAppVersion
#define MyAppVersion "1.0.127"
#endif
#define MyAppPublisher "Hisab Kitab Works"
#define MyAppExeName "HISAB KITAB WORKS License Generator.exe"
#define MySourceDir "..\publish\license-generator-win-x64"

[Setup]
AppId={{AC8B31F9-1562-46B4-B0D9-7FA74A1438EA}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppComments=Developer-only license administration tool
DefaultDirName={autopf}\HISAB KITAB WORKS\Developer Tools\License Generator
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\release
OutputBaseFilename=HISAB_KITAB_WORKS_License_Generator_Setup_{#MyAppVersion}
SetupIconFile=..\..\developer-only\HISAB-KITAB-LICENSE-GENERATOR\Assets\HisabKitab.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Installer
VersionInfoProductName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent unchecked
