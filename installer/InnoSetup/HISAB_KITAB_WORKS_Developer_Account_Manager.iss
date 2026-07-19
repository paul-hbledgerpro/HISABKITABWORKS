#define MyAppName "HISAB KITAB WORKS Account Manager"
#define MyAppVersion "1.0.100"
#define MyAppPublisher "Hisab Kitab Works"
#define MyAppExeName "HISAB KITAB WORKS Client Account Manager.exe"
#define MySourceDir "..\publish\account-manager-win-x64"

[Setup]
AppId={{15F07D5A-F831-4AD1-B408-B57F30B5B5BB}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppComments=Developer-only client accounts, subscriptions, payments, and invoices tool
DefaultDirName={autopf}\HISAB KITAB WORKS\Developer Tools\Account Manager
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\release
OutputBaseFilename=HISAB_KITAB_WORKS_Account_Manager_Setup_1.0.100
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
VersionInfoVersion=1.0.100.0
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
