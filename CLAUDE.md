# CLAUDE.md

Guía para trabajar en este repositorio. La documentación para usuarios está en [README.md](README.md);
el protocolo y la vinculación, en [docs/PROTOCOL.md](docs/PROTOCOL.md) y [docs/PAIRING.md](docs/PAIRING.md).

## Qué es

Susurro: mensajes breves (≤ 300 caracteres) entre **dos** PCs Windows de la misma LAN, mostrados como
un subtítulo overlay que no roba el foco. P2P directo por TCP, sin servidor, sin nube, sin historial.

## Comandos

```bash
dotnet build Susurro.sln                      # Debug
dotnet test tests/Susurro.Core.Tests          # xUnit (incluye tests con sockets TCP/UDP reales)
dotnet test tests/Susurro.Core.Tests --filter "FullyQualifiedName~PairingTests"   # un grupo
dotnet publish src/Susurro.App -c Release -p:PublishProfile=win-x64   # → artifacts/publish/win-x64/Susurro.exe
powershell -ExecutionPolicy Bypass -File scripts/build.ps1            # tests + publish + instalador (Inno Setup)
powershell -ExecutionPolicy Bypass -File scripts/run-two-instances.ps1 [-Reset]   # dos instancias locales (perfiles A/B)
```

- SDK .NET 8 fijado en [global.json](global.json). La versión del producto está en [Directory.Build.props](Directory.Build.props).
- Solo compila/ejecuta en Windows (WPF, Win32, DPAPI).
- `artifacts/` es salida de compilación (ignorado por git).

## Arquitectura

- **`src/Susurro.Core`** (`net8.0`, sin UI): configuración, logging, protocolo, cifrado, vinculación,
  descubrimiento, conexión/reconexión (`PeerLink`), cola de mensajes. Todo lo testeable vive aquí.
  `InternalsVisibleTo` a los tests.
- **`src/Susurro.App`** (`net8.0-windows`, WPF): UI. [AppController.cs](src/Susurro.App/AppController.cs)
  es el único punto que une red ↔ UI y **pasa los eventos de red al hilo de UI**; las vistas solo hablan con él.
- **`tests/Susurro.Core.Tests`**: solo prueba Core. No hay tests de la App.

Flujo: `MainWindow → AppController → PeerLink.Send → bandeja de salida → PeerSession (AES-GCM/TCP) → … →
PeerLink (valida, dedup, ack) → AppController → OverlayController → OverlayHost`.

## Reglas del proyecto (no romper)

- **Core no depende de WPF** ni de APIs de Windows de UI. Lógica nueva → en Core, con tests.
- **Cero CPU en reposo**: nada de sondeo, bucles ni temporizadores periódicos. Usar I/O asíncrona,
  temporizadores de un disparo y eventos. Nada de tráfico de red periódico (solo latido tras 30 s sin tráfico).
- **Bajo consumo de memoria**: GC de estación sin concurrencia, overlay y ventanas que se destruyen al cerrarse,
  sin WinForms (bandeja con `Shell_NotifyIcon` propio), JSON con source-gen ([SusurroJson.cs](src/Susurro.Core/Protocol/SusurroJson.cs)).
- **Publicación**: single-file autocontenido, **sin compresión** (duplica la RAM) y **sin trimming** (WPF no lo admite).
- **El overlay nunca toma el foco**: `HwndSource` con `WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_LAYERED |
  WS_EX_TOOLWINDOW | WS_EX_TOPMOST` y `SW_SHOWNOACTIVATE`. Medido en píxeles físicos con DPI PerMonitorV2.
- **Seguridad**: no confiar en la IP ni en datos de descubrimiento; la identidad se verifica con la clave de vínculo
  (HMAC mutuo + AES-256-GCM por sesión). Validar tamaños de trama **antes** de reservar memoria.
  La clave en disco se protege con DPAPI. Cambios de protocolo → actualizar [docs/PROTOCOL.md](docs/PROTOCOL.md).
- **Privacidad**: no persistir mensajes ni loguear su contenido. Mensajes en espera: solo en memoria, 2 min, máx. 20.
- Sin sonidos, sin ventanas emergentes, sin frameworks visuales: el estilo está todo en [Themes/Dark.xaml](src/Susurro.App/Themes/Dark.xaml).
- Win32 P/Invoke centralizado en [Native/NativeMethods.cs](src/Susurro.App/Native/NativeMethods.cs).

## Convenciones

- Idioma: **español rioplatense** (voseo) en UI, comentarios, XML docs, logs, commits y documentación.
- C# 12, `Nullable` habilitado. `ImplicitUsings` **deshabilitado en la App** (usings explícitos) y habilitado en Core/tests.
- Clases `sealed`/`internal` por defecto; campos privados `_camelCase`; namespaces con ámbito de archivo.
- Puertos por defecto: TCP **47810**, UDP descubrimiento **47811** (multicast `239.255.77.77` + broadcast).
- Datos en `%LOCALAPPDATA%\Susurro\` (`settings.json`, `logs\`); perfiles de desarrollo en `profiles\<nombre>\`.
- Si cambian características, cantidad de tests o cifras, actualizar el README (badge de tests incluido).
