# CLAUDE.md

Guía para trabajar en este repositorio. La documentación para usuarios está en [README.md](README.md);
el protocolo y la identidad, en [docs/PROTOCOL.md](docs/PROTOCOL.md) y [docs/IDENTIDAD.md](docs/IDENTIDAD.md).

## Qué es

Susurro: mensajes breves (≤ 300 caracteres), imágenes (Ctrl+V) y archivos entre las PCs Windows de una
oficina (misma LAN), mostrados como un subtítulo overlay que no roba el foco (los archivos, como tarjetas
arriba a la izquierda para descargar o cerrar), con aviso de "está escribiendo". Cada persona elige a quién escribirle (o a todos los
conectados). P2P directo por TCP en malla, sin servidor, sin nube, sin historial, **sin códigos**: las
PCs se encuentran solas y cada una se identifica con su clave pública. Se actualiza sola desde las
releases de GitHub, sin avisar (lema: *Susurro, para evitar los gritos en la oficina*).

## Comandos

```bash
dotnet build Susurro.sln                      # Debug
dotnet test tests/Susurro.Core.Tests          # xUnit (incluye tests con sockets TCP/UDP reales)
dotnet test tests/Susurro.Core.Tests --filter "FullyQualifiedName~PeerLinkIntegrationTests"   # un grupo
dotnet publish src/Susurro.App -c Release -p:PublishProfile=win-x64   # → artifacts/publish/win-x64/Susurro.exe
powershell -ExecutionPolicy Bypass -File scripts/build.ps1            # tests + publish + instalador (Inno Setup)
powershell -ExecutionPolicy Bypass -File scripts/run-two-instances.ps1 [-Reset]   # dos instancias locales (perfiles A/B)
git tag v2.3.0 && git push origin v2.3.0     # release: antes subir <Version> en Directory.Build.props (el CI lo exige)
```

- CI en [.github/workflows/ci.yml](.github/workflows/ci.yml) (Windows): tests, publicación e instalador en cada
  push/PR; con un tag `vX.Y.Z` crea la release con `Susurro.exe`, `SusurroSetup.exe` y `susurro-update.json`.

- SDK .NET 8 fijado en [global.json](global.json). La versión del producto está en [Directory.Build.props](Directory.Build.props).
- Solo compila/ejecuta en Windows (WPF, Win32, DPAPI).
- `artifacts/` es salida de compilación (ignorado por git).
- Para simular una oficina: más perfiles con `Susurro.exe --profile C --port 47840`. Un perfil nuevo
  pide nombre en la bienvenida; para saltearla, crear `%LOCALAPPDATA%\Susurro\profiles\<p>\settings.json`
  con `{ "schemaVersion": 2, "friendlyName": "Ana", "setupCompleted": true, "startWithWindows": false }`.

## Arquitectura

- **`src/Susurro.Core`** (`net8.0`, sin UI): configuración, logging, protocolo, cifrado, identidad
  (`Identity/LocalIdentity`), descubrimiento, contactos y conexiones (`Net/PeerLink`), cola de mensajes.
  Todo lo testeable vive aquí. `InternalsVisibleTo` a los tests.
- **`src/Susurro.App`** (`net8.0-windows`, WPF): UI. [AppController.cs](src/Susurro.App/AppController.cs)
  es el único punto que une red ↔ UI y **pasa los eventos de red al hilo de UI**; las vistas solo hablan con él.
- **`tests/Susurro.Core.Tests`**: solo prueba Core. No hay tests de la App.

Flujo: `MainWindow (Para: persona | todos) → AppController → PeerLink.Send (una copia por destinatario) →
bandeja de salida → PeerSession (AES-GCM/TCP) → … → PeerLink (valida, dedup, ack) → AppController →
OverlayController → OverlayHost`.

Claves del modelo:
- `InstanceId` = SHA-256(clave pública)[0..16]. Clave de enlace por par = HKDF(ECDH estático). Sobre ella
  corre el saludo HMAC + AES-GCM de siempre. Protocolo **v2** (incompatible con la v1 de códigos).
- `PeerLink` tiene un `Peer` por contacto (sesión, pista de dirección, bucle de conexión propio que
  **duerme** hasta un evento). Un contacto se crea solo tras completar el saludo; los datagramas UDP
  de ids desconocidos disparan un intento de conexión con límites (`MaxConcurrentProbes`, `MaxQueuedProbes`).
- Sin nombre elegido (`AppController.IsReady`) no se llama a `Link.Start()`: nunca se muestra el nombre del equipo.
- Actualización (`Core/Updates/Updater`): manifiesto `susurro-update.json` de la última release → descarga a
  `Susurro.exe.download` → tamaño + SHA-256 → el `.exe` en uso pasa a `.old` y el nuevo toma su nombre. La App
  (`AppController`, sección "actualización automática") reinicia con `--updated PID` solo cuando no se nota; la
  nueva avisa por un evento con nombre y, si no avisa en 60 s, `Updater.Rollback`.
- Atajos: la ventana `Views/ShortcutsWindow` (F1, botón ⌨, menú de la bandeja) lista todos; si agregás un
  atajo, sumalo ahí y al README.

## Reglas del proyecto (no romper)

- **Core no depende de WPF** ni de APIs de Windows de UI. Lógica nueva → en Core, con tests.
- **Cero CPU en reposo**: nada de sondeo, bucles ni temporizadores periódicos. Usar I/O asíncrona,
  temporizadores de un disparo y eventos. Nada de tráfico de red periódico: con compañeros apagados no se
  reintenta salvo que haya mensajes esperando o se haya perdido la conexión hace poco (`RetryWindow`).
  El único tráfico en reposo es el latido tras 30 s sin tráfico por sesión, y fuera de la LAN solo la
  búsqueda de actualizaciones: una consulta HTTPS a GitHub por día (temporizador de un disparo).
- **Bajo consumo de memoria**: GC de estación sin concurrencia, overlay y ventanas que se destruyen al cerrarse,
  sin WinForms (bandeja con `Shell_NotifyIcon` propio), JSON con source-gen ([SusurroJson.cs](src/Susurro.Core/Protocol/SusurroJson.cs)).
- **Publicación**: single-file autocontenido, **sin compresión** (duplica la RAM) y **sin trimming** (WPF no lo admite).
- **El overlay nunca toma el foco**: `HwndSource` con `WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_LAYERED |
  WS_EX_TOOLWINDOW | WS_EX_TOPMOST` y `SW_SHOWNOACTIVATE`. Medido en píxeles físicos con DPI PerMonitorV2.
  Única excepción al click-through: los mensajes **importantes** (`Urgent` en el protocolo) no llevan
  `WS_EX_TRANSPARENT` y se cierran con un clic, pero siguen sin activarse (`MA_NOACTIVATE`). No se van
  solos, y su `ack shown` ("Visto") se envía al hacer clic.
- **Imágenes y archivos** (`Core/Transfers/TransferManager`): por la sesión cifrada en bloques de 32 KB;
  trama de sesión ≤ 64 KB. Solo con quien anunció la capacidad `files` en el saludo (compatibilidad con la
  2.0.0 sin subir la versión). Validar offset/tamaño/hash; limpiar siempre el nombre con `FileNames.Sanitize`;
  nunca dejar archivos a medias en el destino (temporal + mover). Las ventanas de archivos/imágenes reciben
  clics pero no se activan (igual que los importantes).
- **Seguridad**: no confiar en la IP, en el nombre ni en datos de descubrimiento; verificar siempre
  `id == hash(clave)` y la prueba HMAC antes de aceptar a alguien. Validar tamaños de trama **antes** de
  reservar memoria. La clave privada en disco se protege con DPAPI. Cambios de protocolo → subir
  `ProtocolConstants.Version` y actualizar [docs/PROTOCOL.md](docs/PROTOCOL.md).
- **Actualizaciones**: descargar solo por HTTPS desde `github.com`/`*.githubusercontent.com`; verificar tamaño
  y SHA-256 **antes** de tocar el ejecutable; nunca dejar un `.exe` a medias; reiniciar solo sin ventanas,
  sin nada en pantalla ni pendiente y con la persona inactiva. El instalador deja `{app}` con `users-modify`
  para que se pueda reemplazar sin administrador.
- **Privacidad**: no persistir mensajes ni loguear su contenido. Mensajes en espera: solo en memoria, 2 min, máx. 20 por persona.
- Sin sonidos, sin ventanas emergentes, sin frameworks visuales: el estilo está todo en [Themes/Dark.xaml](src/Susurro.App/Themes/Dark.xaml).
- Win32 P/Invoke centralizado en [Native/NativeMethods.cs](src/Susurro.App/Native/NativeMethods.cs).

## Convenciones

- Idioma: **español rioplatense** (voseo) en UI, comentarios, XML docs, logs, commits y documentación.
- C# 12, `Nullable` habilitado. `ImplicitUsings` **deshabilitado en la App** (usings explícitos) y habilitado en Core/tests.
- Clases `sealed`/`internal` por defecto; campos privados `_camelCase`; namespaces con ámbito de archivo.
- Puertos por defecto: TCP **47810**, UDP descubrimiento **47811** (multicast `239.255.77.77` + broadcast).
- Datos en `%LOCALAPPDATA%\Susurro\` (`settings.json`, `logs\`); perfiles de desarrollo en `profiles\<nombre>\`.
- `settings.json` tiene `schemaVersion`; al cambiar su forma, migrar en `SettingsValidator.Normalize`.
- Si cambian características, cantidad de tests o cifras, actualizar el README (badge de tests incluido).
