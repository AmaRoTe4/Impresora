; Script de Inno Setup personalizado para Tienda Web Printers
; 100% Gratuito y Libre - Optimizado para Red Local
; Rutas relativas a este .iss: se espera que los binarios publicados
; (dotnet publish -o out) y los logos vivan junto al script (repo root).

[Setup]
AppName=Tienda Web Printers
AppVersion=1.0
; Instala en la carpeta del usuario actual (sin admin), asi printagent.env
; y el propio directorio de instalacion quedan siempre escribibles por la
; app sin necesitar privilegios elevados.
DefaultDirName={localappdata}\Tienda Web Printers
DefaultGroupName=Tienda Web Printers
UninstallDisplayIcon={app}\PrintAgent.exe
PrivilegesRequired=lowest
Compression=lzma2
SolidCompression=yes
OutputDir=.\dist
OutputBaseFilename=Instalador_Tienda_Web_Printers

; --- CONFIGURACIÓN DEL LOGO Y APARIENCIA ---
WizardImageFile=logo_grande.bmp
WizardSmallImageFile=logo_chico.bmp

[Files]
Source: "out\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Tienda Web Printers"; Filename: "{app}\PrintAgent.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Tienda Web Printers"; Filename: "{app}\PrintAgent.exe"; WorkingDir: "{app}"; Tasks: desktopicon
; Arranque automático leyendo correctamente el printagent.env modificado
Name: "{userstartup}\Tienda Web Printers"; Filename: "{app}\PrintAgent.exe"; WorkingDir: "{app}"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Accesos directos adicionales:"

[Run]
; --- REGLAS DEL FIREWALL (Abren el puerto 5000 TCP de entrada y salida) ---
; OJO: como el instalador corre con PrivilegesRequired=lowest (sin UAC),
; estos netsh van a fallar en silencio si el usuario no es administrador.
; Si Windows Firewall bloquea las conexiones LAN, corre el instalador una
; vez como administrador (click derecho > Ejecutar como administrador) o
; agrega la regla a mano:
;   netsh advfirewall firewall add rule name="Tienda Web Printers Entrada" dir=in action=allow protocol=TCP localport=5000 profile=private,public enable=yes
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Tienda Web Printers Entrada"" dir=in action=allow protocol=TCP localport=5000 profile=private,public enable=yes"; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Tienda Web Printers Salida"" dir=out action=allow protocol=TCP localport=5000 profile=private,public enable=yes"; Flags: runhidden
