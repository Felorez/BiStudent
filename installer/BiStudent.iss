; Сборка: dotnet publish клиента в out\Client и сервера в out\Server, installer\BiStudent.cmd в out\,
; затем ISCC /DSrc=<путь к out> /DOutDir=<куда положить exe> /DAppVersion=<версия> installer\BiStudent.iss
#ifndef Src
  #define Src AddBackslash(SourcePath) + "..\out"
#endif
#ifndef OutDir
  #define OutDir AddBackslash(SourcePath) + "..\dist"
#endif
#ifndef AppVersion
  #define AppVersion "1.0"
#endif

[Setup]
AppId={{6B1F2C3A-8E44-4F0B-9D1E-B15DE7E1A001}
AppName=BiStudent
AppVersion={#AppVersion}
AppPublisher=BiStudent
DefaultDirName={localappdata}\Programs\BiStudent
DefaultGroupName=BiStudent
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutDir}
OutputBaseFilename=BiStudent-Setup
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\Client\BiStudent.exe
WizardStyle=modern
CloseApplications=force

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Ярлык на рабочем столе"; GroupDescription: "Дополнительно:"

[Files]
Source: "{#Src}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\BiStudent"; Filename: "{app}\BiStudent.cmd"; WorkingDir: "{app}"; IconFilename: "{app}\Client\BiStudent.exe"; Flags: runminimized
Name: "{group}\Удалить BiStudent"; Filename: "{uninstallexe}"
Name: "{autodesktop}\BiStudent"; Filename: "{app}\BiStudent.cmd"; WorkingDir: "{app}"; IconFilename: "{app}\Client\BiStudent.exe"; Flags: runminimized; Tasks: desktopicon

[Run]
Filename: "{app}\BiStudent.cmd"; WorkingDir: "{app}"; Description: "Запустить BiStudent"; Flags: postinstall nowait skipifsilent runminimized shellexec

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im BiStudent.Server.exe"; Flags: runhidden; RunOnceId: "StopServer"
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im BiStudent.exe"; Flags: runhidden; RunOnceId: "StopClient"
