#define MyAppName "Obxodka"

; =========================================================================
; Dynamic Version Extraction from version.props (Inno Setup Preprocessor)
; =========================================================================
#ifndef MyAppVersion
  #define PropsPath SourcePath + "\..\version.props"
  #if FileExists(PropsPath)
    #define FileHandle FileOpen(PropsPath)
    #define LineText ""
    #sub ParsePropsLine
      #if Pos("<AppVersion>", LineText) > 0
        #define StartIdx Pos("<AppVersion>", LineText) + 12
        #define EndIdx Pos("</AppVersion>", LineText)
        #expr MyAppVersion = Copy(LineText, StartIdx, EndIdx - StartIdx)
      #endif
    #endsub
    #for {FileHandle; FileHandle && !FileEof(FileHandle); LineText = FileRead(FileHandle)} ParsePropsLine
    #if FileHandle
      #expr FileClose(FileHandle)
    #endif
  #endif
#endif

; Fallback if not resolved
#ifndef MyAppVersion
  #define MyAppVersion "6.0.9"
#endif

#define MyAppPublisher "OctoCore"
#define MyAppURL "https://github.com/OctoCore-Dev/obxodka"
#define MyAppExeName "obxodka.Maui.exe"

[Setup]
AppId={{9F7B6B0E-562F-4E38-9B21-C7D813F4E231}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductName={#MyAppName}
VersionInfoCompany={#MyAppPublisher}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableProgramGroupPage=yes
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
OutputDir=..\installer-output
OutputBaseFilename=Obxodka-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=appicon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startupicon"; Description: "Запускать Obxodka при входе в Windows"; GroupDescription: "Автозапуск:"

[Files]
Source: "..\win-unpackaged\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
