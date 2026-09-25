; ============================================================================
;  Instalador de Susurro (Inno Setup 6)  →  artifacts\installer\SusurroSetup.exe
;
;  Requisito solo para GENERAR el instalador: Inno Setup 6 (https://jrsoftware.org).
;  Uso:   1) dotnet publish src/Susurro.App -c Release -p:PublishProfile=win-x64
;         2) "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\Susurro.iss
;  O simplemente:  .\scripts\build.ps1
; ============================================================================

#define AppName "Susurro"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppExe "Susurro.exe"

[Setup]
AppId={{8F3B2A61-5C1E-4D7A-9B0E-2C4F6A1D3E57}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=SusurroSetup
SetupIconFile=..\src\Susurro.App\Assets\Susurro.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; Administrador: instala en Archivos de programa y crea la regla de firewall.
PrivilegesRequired=admin
; Cierra Susurro si está abierto al instalar/actualizar/desinstalar.
AppMutex=Local\Susurro.Instance
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "autostart"; Description: "Iniciar Susurro con Windows (queda en la bandeja)"
Name: "firewall"; Description: "Permitir Susurro en el Firewall de Windows (redes privadas y de dominio)"
Name: "firewall\public"; Description: "También en redes públicas (solo si la red de la oficina figura como pública)"; Flags: unchecked
Name: "desktopicon"; Description: "Crear acceso directo en el escritorio"; Flags: unchecked

[Dirs]
; La actualización automática reemplaza Susurro.exe sin pedir permisos de administrador (y sin cambiar
; la ruta, así la regla de firewall sigue valiendo): la carpeta del programa admite escritura de los usuarios.
Name: "{app}"; Permissions: users-modify

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Permissions: users-modify

[UninstallDelete]
; Restos de una actualización automática (copia anterior, descarga, versión descartada).
Type: files; Name: "{app}\{#AppExe}.old"
Type: files; Name: "{app}\{#AppExe}.download"
Type: files; Name: "{app}\{#AppExe}.bad"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Regla de firewall por programa (cubre TCP 47810 y UDP 47811 aunque se cambie el puerto).
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Susurro"""; Flags: runhidden; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Susurro"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=private,domain"; Flags: runhidden; Tasks: firewall and not firewall\public
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Susurro"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=any"; Flags: runhidden; Tasks: firewall\public
; El inicio con Windows es por usuario (HKCU\...\Run): lo configura la propia app como el usuario original.
Filename: "{app}\{#AppExe}"; Parameters: "--set-autostart on"; Flags: runasoriginaluser runhidden waituntilterminated; Tasks: autostart
Filename: "{app}\{#AppExe}"; Parameters: "--set-autostart off"; Flags: runasoriginaluser runhidden waituntilterminated; Tasks: not autostart
Filename: "{app}\{#AppExe}"; Description: "Abrir Susurro ahora"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "StopSusurro"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Susurro"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveAutostart"

[Code]
// Al desinstalar, ofrece borrar también la configuración, el vínculo y el registro.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\Susurro');
    if DirExists(DataDir) then
      if SuppressibleMsgBox('¿Eliminar también la configuración de Susurro (tu nombre, identidad, contactos y registro)?'#13#10#13#10 +
                  'Si vas a reinstalar, elegí "No" para que tus compañeros te sigan viendo como la misma persona.',
                  mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
