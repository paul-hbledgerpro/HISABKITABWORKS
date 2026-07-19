#define MyAppName "HISAB KITAB WORKS Admin License Generator"
#define MyAppVersion "1.0.103"
#define MyAppPublisher "Hisab Kitab Works"
#define MyAppExeName "HISAB KITAB WORKS License Generator.exe"

[Setup]
AppId={{AC8B31F9-1562-46B4-B0D9-7FA74A1438EA}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\HISAB KITAB WORKS\Admin License Generator
DefaultGroupName={#MyAppName}
OutputDir=installer_output
OutputBaseFilename=HISAB_KITAB_WORKS_Admin_License_Generator_Setup
SetupIconFile=Assets\HisabKitab.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
