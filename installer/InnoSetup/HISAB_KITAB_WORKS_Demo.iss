#define MyAppName "HISAB KITAB WORKS - Client Demo"
#ifndef MyAppVersion
#define MyAppVersion "1.0.161"
#endif
#define MyAppPublisher "Hisab Kitab Works"
#define MyAppExeName "HISAB KITAB.exe"
#define MySourceDir "..\publish\demo-win-x64"

[Setup]
AppId={{B1D247F2-3652-4F1E-8710-875E50D168C9}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppComments=Safe sales demonstration with fictional HISAB KITAB data
DefaultDirName={autopf}\HISAB KITAB WORKS Demo
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\release
OutputBaseFilename=HISAB_KITAB_WORKS_Client_Demo_Setup_{#MyAppVersion}
SetupIconFile=..\..\src\ManagerPaperworkSystem.UI\Assets\HisabKitab.ico
Compression=lzma2/ultra64
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
Name: "desktopicon"; Description: "Create a desktop demo shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Excludes: "\cs\*,\de\*,\es\*,\fr\*,\it\*,\ja\*,\ko\*,\pl\*,\pt-BR\*,\ru\*,\tr\*,\zh-Hans\*,\zh-Hant\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Launch Client Demo"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--demo"
Name: "{group}\Reset Client Demo Data"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--demo-reset"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\HISAB KITAB WORKS - Client Demo"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--demo"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--demo-prepare"; StatusMsg: "Preparing fictional demonstration data..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Parameters: "--demo"; Description: "Launch the HISAB KITAB client demo"; Flags: nowait postinstall skipifsilent
