
; PrintAgent installer script (Inno Setup 6)

[Setup]
AppName=PrintAgent
AppVersion=1.0
DefaultDirName={pf}\PrintAgent
DisableProgramGroupPage=yes
OutputBaseFilename=Setup
Compression=lzma
SolidCompression=yes

[Files]
Source: "PrintAgent.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "agent.ico"; DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "netsh"; Parameters: "http add urlacl url=http://localhost:5000/ user={username}"; StatusMsg: "Configurando URL ACL..."
Filename: "{app}\PrintAgent.exe"; Description: "Lanzar PrintAgent"; Flags: postinstall skipifsilent nowait runhidden

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PrintAgent"; ValueData: {app}\PrintAgent.exe; Flags: uninsdeletevalue
