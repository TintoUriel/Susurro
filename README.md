<div align="center">

<img src="docs/assets/banner.svg" alt="Susurro — Dos PCs. Un mensaje. Cero distracciones." width="100%">

<br>

![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-1c1d21?style=flat-square&logo=windows&logoColor=8aa9d6)
![.NET 8](https://img.shields.io/badge/.NET-8-1c1d21?style=flat-square&logo=dotnet&logoColor=8aa9d6)
![WPF](https://img.shields.io/badge/UI-WPF-1c1d21?style=flat-square)
![LAN P2P](https://img.shields.io/badge/red-LAN%20P2P-1c1d21?style=flat-square)
![Cifrado](https://img.shields.io/badge/cifrado-AES--256--GCM-1c1d21?style=flat-square)
![Tests](https://img.shields.io/badge/tests-80%20OK-6fbf8e?style=flat-square)

**Mensajes breves entre dos PCs de la oficina, que aparecen como un subtítulo discreto.**<br>
Sin servidor · sin nube · sin cuentas · sin sonidos · sin robar el foco

<a href="https://github.com/TintoUriel/Susurro/releases/latest"><img src="https://img.shields.io/badge/Descargar-Susurro.exe-8aa9d6?style=for-the-badge&logo=windows&logoColor=101216&labelColor=e7e8ea" alt="Descargar Susurro.exe"></a>

[Características](#características) · [Capturas](#capturas) · [Inicio rápido](#inicio-rápido) · [Documentación](#índice)

</div>

---

<p align="center">
  <img src="docs/assets/overlay.png" alt="Un mensaje de Susurro mostrado como subtítulo sobre el escritorio" width="820">
</p>

Escribís en una PC y, en la otra, el mensaje aparece unos segundos **por encima de todo**, como un
subtítulo. Quien lo recibe puede seguir escribiendo en Word, en el navegador o en Visual Studio:
Susurro **no toma el foco, no captura el mouse ni el teclado, no aparece en Alt+Tab** y los clics lo atraviesan.

<table>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/assets/ventana.png" alt="Ventana principal de Susurro" width="100%">
    </td>
    <td width="50%" valign="top">

**Una utilidad pequeña, no una aplicación empresarial.**

- <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Espacio</kbd> desde **cualquier programa** abre Susurro listo para escribir
- <kbd>Enter</kbd> envía y te devuelve a lo que estabas · <kbd>Ctrl</kbd>+<kbd>U</kbd> urgente · <kbd>Esc</kbd> oculta
- Máximo 300 caracteres; el campo se limpia y conserva el foco
- `Enviado` → `Entregado ✓` → `Visto ✓`
- Vive en la bandeja del sistema y arranca con Windows
- No guarda historial de mensajes

</td>
  </tr>
</table>

## Características

| | |
|---|---|
| ⌨️ **Atajo global** | <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Espacio</kbd> desde cualquier programa: escribís, <kbd>Enter</kbd>, y volvés a lo que estabas. Configurable. |
| 🪶 **Liviano de verdad** | 0 % de CPU en reposo (sin sondeo ni bucles), ~9 MB residentes en la bandeja, configuración de pocos KB. |
| 🔌 **Directo por la LAN** | Las dos PCs se hablan entre sí por TCP. Sin servidor central, sin internet, sin base de datos. |
| 🔎 **Se encuentran solas** | Descubrimiento automático por UDP; si cambia la IP, se vuelven a encontrar por su identificador. |
| 🔁 **Reconexión automática** | PC apagada, reiniciada, suspendida o red caída: se reconecta sola y entrega lo que quedó en espera. |
| 🔐 **Solo tus dos PCs** | Vinculación con código de un solo uso (ECDH + PBKDF2), autenticación mutua y mensajes cifrados con AES-256-GCM. |
| 🎬 **Overlay tipo subtítulo** | Posición, monitor, duración, tamaño, color (blanco / amarillo / gris), recuadro opcional y contorno de letras. |
| ⚡ **Urgentes discretos** | Una diferencia visual sutil (borde ámbar, negrita o fondo cálido), nunca sonidos ni ventanas emergentes. |
| 🖥️ **Multimonitor y DPI** | Monitor automático, principal o específico; nítido al 100, 125, 150 y 200 %; el texto nunca se corta. |
| ♿ **Accesible** | Tamaño de fuente, alto contraste, animaciones desactivables; respeta la configuración de Windows. |

## Capturas

<table>
  <tr>
    <td align="center" width="50%">
      <img src="docs/assets/overlay-cine.png" alt="Estilo cine: texto amarillo sin recuadro" width="100%"><br>
      <sub><b>Estilo cine</b> — amarillo, sin recuadro y con contorno</sub>
    </td>
    <td align="center" width="50%">
      <img src="docs/assets/urgente.png" alt="Mensaje urgente" width="100%"><br>
      <sub><b>Urgente</b> — distinto, pero igual de discreto</sub>
    </td>
  </tr>
  <tr>
    <td align="center">
      <img src="docs/assets/apariencia.png" alt="Configuración de apariencia con vista previa" width="92%"><br>
      <sub><b>Apariencia</b> con vista previa en vivo</sub>
    </td>
    <td align="center">
      <img src="docs/assets/config-overlay.png" alt="Configuración de ubicación y duración del overlay" width="92%"><br>
      <sub><b>Overlay</b> — monitor, posición y duración</sub>
    </td>
  </tr>
  <tr>
    <td align="center">
      <img src="docs/assets/vincular.png" alt="Vinculación con código de un solo uso" width="92%"><br>
      <sub><b>Vinculación</b> con código de un solo uso</sub>
    </td>
    <td valign="middle">

### Inicio rápido

1. Descargá [**Susurro.exe**](https://github.com/TintoUriel/Susurro/releases/latest) y abrilo en **las dos PCs**.
2. En una PC: **Mostrar código**. En la otra: **Ingresar código**.
3. Listo. Desde ahora se conectan solas cada vez que se encienden.

Detalles en [Cómo instalar](#5-cómo-instalar) y [Cómo vincular](#6-cómo-vincular-las-dos-pcs).

</td>
  </tr>
</table>

---

## Índice

1. [Qué es Susurro](#características)
2. [Arquitectura](#2-arquitectura)
3. [Cómo compilar](#3-cómo-compilar)
4. [Cómo publicar](#4-cómo-publicar)
5. [Cómo instalar](#5-cómo-instalar)
6. [Cómo vincular las dos PCs](#6-cómo-vincular-las-dos-pcs)
7. [Firewall de Windows](#7-firewall-de-windows)
8. [Cómo funciona el descubrimiento](#8-cómo-funciona-el-descubrimiento)
9. [Cómo funciona la reconexión](#9-cómo-funciona-la-reconexión)
10. [Cómo cambiar el puerto](#10-cómo-cambiar-el-puerto)
11. [Solución de problemas de conexión](#11-solución-de-problemas-de-conexión)
12. [Dos instancias en la misma PC (desarrollo)](#12-dos-instancias-en-la-misma-pc-desarrollo)

Anexos: [Uso](#uso) · [Overlay](#el-overlay) · [Configuración](#configuración) · [Rendimiento](#rendimiento)
· [Tests](#tests) · [Decisiones técnicas](#decisiones-técnicas) · [Protocolo](docs/PROTOCOL.md) · [Vinculación](docs/PAIRING.md)

---

## 2. Arquitectura

```
Susurro.sln
├─ src/Susurro.Core          (net8.0, sin UI — probado con tests)
│  ├─ Config/       AppSettings, SettingsValidator, SettingsStore (JSON atómico)
│  ├─ Logging/      Log + FileLogSink (256 KB con rotación)
│  ├─ Protocol/     Packet, FrameIO, SecureChannel (AES-GCM), HandshakeCrypto, JSON con source-gen
│  ├─ Pairing/      PairingCode, PairingCrypto (ECDH + PBKDF2 + HKDF), PairingInvitation, DPAPI
│  ├─ Discovery/    DiscoveryService (UDP multicast + broadcast)
│  ├─ Net/          PeerLink (conexión, reconexión, bandeja de salida), PeerSession, SessionArbiter, Backoff
│  └─ Messaging/    MessageRules, DuplicateFilter, DisplayQueue, WhisperMessage
├─ src/Susurro.App           (net8.0-windows, WPF)
│  ├─ AppController.cs  une todo; pasa eventos de red al hilo de UI
│  ├─ Overlay/      OverlayHost (HwndSource Win32), SubtitleVisual, OverlayController, MonitorService
│  ├─ Tray/         TrayIcon (Shell_NotifyIcon nativo, sin WinForms)
│  ├─ Views/        MainWindow, SettingsWindow, PairingWindow, LogWindow
│  ├─ Services/     AutoStart (HKCU\Run), SingleInstance, CommandLine, WindowStyling, MemoryTrimmer
│  └─ Themes/Dark.xaml  todo el estilo propio (sin frameworks visuales)
├─ tests/Susurro.Core.Tests  (xUnit)
├─ installer/Susurro.iss     (Inno Setup → SusurroSetup.exe)
├─ scripts/                  build, dos instancias, instalación portátil, firewall, icono
└─ docs/                     PROTOCOL.md, PAIRING.md
```

Flujo de un mensaje:

```
MainWindow ─Send→ AppController ─→ PeerLink.Send ─→ bandeja de salida ─→ PeerSession (AES-GCM/TCP)
                                                                               │ LAN
PeerSession ─→ PeerLink (valida, descarta duplicados, ack) ─→ AppController ─→ OverlayController
                                                                      └─→ OverlayHost (subtítulo)
```

`Susurro.Core` no depende de WPF: toda la lógica de red, protocolo, vinculación, cola y
configuración se prueba con tests, incluidas conexiones TCP reales entre dos instancias.

## 3. Cómo compilar

Requisitos: Windows 10/11 y el **SDK de .NET 8** (`winget install Microsoft.DotNet.SDK.8`).

```bash
dotnet build Susurro.sln
```

```bash
dotnet build Susurro.sln -c Release
```

```bash
dotnet test tests/Susurro.Core.Tests
```

Ejecutable de Debug: `src/Susurro.App/bin/Debug/net8.0-windows/Susurro.exe`.

## 4. Cómo publicar

Publicación final **autocontenida** (no requiere instalar .NET en las PCs):

```bash
dotnet publish src/Susurro.App -c Release -p:PublishProfile=win-x64
```

Resultado: **un único** `artifacts/publish/win-x64/Susurro.exe` (~140 MB, con el runtime de .NET
incluido). Se puede copiar tal cual a otra PC y ejecutar.

> ¿Por qué sin comprimir? Comprimido pesaría ~64 MB, pero medido en reposo usa el doble de memoria
> privada (110 MB contra 53 MB), porque descomprime los ensamblados en memoria. Se prioriza el consumo.

Todo en un paso (tests + publicación + zip portátil + instalador si está Inno Setup):

```bash
powershell -ExecutionPolicy Bypass -File scripts/build.ps1
```

> Alternativa mínima: si las PCs ya tienen el *.NET 8 Desktop Runtime*, se puede publicar
> dependiente del framework (~1 MB):
> `dotnet publish src/Susurro.App -c Release -r win-x64 --self-contained false -o artifacts/fdd`

## 5. Cómo instalar

**La forma más simple — un solo `.exe`**: descargá `Susurro.exe` desde
[Releases](https://github.com/TintoUriel/Susurro/releases/latest), guardalo en una carpeta fija
(por ejemplo `C:\Programas\Susurro\`) y abrilo. No necesita instalar nada más: la primera vez
pide el nombre de la PC y la vinculación, y queda configurado para iniciar con Windows.

> Windows puede mostrar *"Windows protegió tu PC"* porque el ejecutable no está firmado
> digitalmente: **Más información → Ejecutar de todas formas**. Y la primera vez, el aviso del
> Firewall: **Permitir** (ver [sección 7](#7-firewall-de-windows)).

**Opción A — instalador** (`artifacts/installer/SusurroSetup.exe`, generado por `scripts/build.ps1`
si tenés [Inno Setup 6](https://jrsoftware.org/isdl.php)):

- Instala en *Archivos de programa*, crea el acceso directo del menú Inicio (y opcional el de escritorio).
- Opciones: *Iniciar con Windows* (marcada), *Permitir en el Firewall* (marcada).
- Se desinstala desde *Configuración de Windows → Aplicaciones*; quita la regla de firewall, el
  inicio automático y, si lo confirmás, la configuración.

**Opción B — script de instalación** (junto a `Susurro.exe`, desde `scripts/`): ejecutar

```bash
powershell -ExecutionPolicy Bypass -File install-portable.ps1 -AutoStart -Firewall
```

Se instala en `%LOCALAPPDATA%\Programs\Susurro` y aparece en *Aplicaciones instaladas* para
desinstalarlo (`install-portable.ps1 -Uninstall`).

**Inicio con Windows**: viene **activado**. Windows inicia → Susurro arranca oculto en la bandeja
→ se conecta solo. Se desactiva en *Configuración → General → Iniciar con Windows*.

## 6. Cómo vincular las dos PCs

La primera vez que se abre Susurro aparece la pantalla de bienvenida (también en *Vincular…* o
*Configuración → Conexión → Volver a vincular…*).

1. En cada PC, poné un nombre amigable (por ejemplo **Tinto** y **Oficina**).
2. En la PC A: **Mostrar código → Generar código** → aparece `XXXX-XXXX` (5 minutos, un solo uso).
3. En la PC B: **Ingresar código** → elegí la PC A de la lista (se detecta sola) o escribí su IP o
   nombre de equipo → escribí el código → **Vincular**.
4. Ambas muestran "✓ Vinculado". Listo: desde ahora se conectan solas.

Detalles criptográficos y modelo de amenazas: [docs/PAIRING.md](docs/PAIRING.md).

## 7. Firewall de Windows

Susurro necesita recibir conexiones entrantes en **TCP 47810** y **UDP 47811**.

- El **instalador** crea una regla por programa (redes privadas y de dominio).
- Sin instalador, la primera vez Windows pregunta *"¿Quieres permitir que las redes… accedan a esta
  aplicación?"* → **Permitir** (al menos en redes privadas).
- Manual (PowerShell como administrador):

```bash
powershell -ExecutionPolicy Bypass -File scripts/firewall.ps1 -Exe "C:\Program Files\Susurro\Susurro.exe"
```

- Si la red de la oficina está marcada como **Pública**, cambiala a *Privada* (Configuración →
  Red e Internet → propiedades de la red) o usá `-AllProfiles` / la opción "También en redes públicas".

## 8. Cómo funciona el descubrimiento

- Cada Susurro escucha en **UDP 47811** (multicast `239.255.77.77` y broadcast). Escuchar no consume
  CPU: la recepción es asíncrona.
- **No hay tráfico periódico**. Solo se envía:
  - un *anuncio* al iniciar, al cambiar la red, al volver de suspensión o al generar un código;
  - una *consulta* (3 datagramas en 1,5 s) cuando hace falta encontrar a la otra PC.
- Las respuestas incluyen `InstanceId`, nombre y puerto TCP. Esa información **no es confiable**:
  solo aporta direcciones candidatas; la identidad se verifica criptográficamente al conectar.
- Si el descubrimiento no funciona en tu red (VLAN distintas, multicast bloqueado), escribí la IP o
  el nombre de la otra PC en *Configuración → Conexión → Dirección de la otra PC*.

## 9. Cómo funciona la reconexión

- Cualquiera de las dos PCs puede iniciar la conexión; si ambas lo hacen a la vez, una regla
  determinista elige la misma en los dos lados.
- Sin conexión, un único bucle prueba: última IP conocida → dirección manual → descubrimiento, con
  esperas crecientes **1, 2, 5, 10, 20, 30, 60 s** (máximo 1 intento por minuto con la otra PC apagada).
- Se reintenta **al instante** cuando la otra PC se anuncia (acaba de encenderse), cambia la red
  local, el equipo vuelve de suspensión o pulsás *Reconectar ahora*.
- **Cambio de IP**: la identidad es el `InstanceId`, no la IP; el descubrimiento encuentra la nueva
  dirección y la guarda como "última conocida".
- **Detección de caída**: `bye` inmediato si la otra PC cierra Susurro o Windows; latido solo si
  hay 30 s sin tráfico; conexión muerta tras 75 s sin respuesta.
- Los mensajes enviados sin conexión esperan hasta 2 minutos y se entregan al reconectar.

Detalle completo: [docs/PROTOCOL.md](docs/PROTOCOL.md).

## 10. Cómo cambiar el puerto

*Configuración → Conexión → Puerto TCP* → Guardar. La comunicación se reinicia sola.

- Cada PC puede usar su propio puerto: la otra lo aprende al conectarse o por descubrimiento.
- Si el descubrimiento no funciona y cambiaste el puerto, poné `IP:puerto` en la dirección manual
  de la otra PC.
- La regla de firewall del instalador es **por programa**, así que sigue valiendo con otro puerto.
- Desde la línea de comandos: `Susurro.exe --port 50000` (se guarda). El UDP de descubrimiento se
  cambia con `--discovery-port` (debe ser el mismo en ambas PCs).

## 11. Solución de problemas de conexión

| Síntoma | Qué hacer |
|---|---|
| **× Desconectado** permanente | ¿Está Susurro abierto en la otra PC? ¿Están en la misma red? Probá *Configuración → Conexión → Reconectar ahora*. |
| "La otra PC responde, pero el puerto TCP no es accesible (¿firewall?)" | Falta la regla de firewall en la otra PC ([sección 7](#7-firewall-de-windows)). |
| "La otra PC no reconoce este vínculo" | Una de las dos se desvinculó o se reinstaló: volvé a vincular. |
| "El puerto 47810 está en uso por otro programa" | Cambiá el puerto ([sección 10](#10-cómo-cambiar-el-puerto)). |
| La otra PC no aparece en la lista al vincular | Multicast/broadcast bloqueado: escribí su IP (en la otra PC: *Configuración → Conexión → Esta PC*). |
| Se conecta y se desconecta | Revisá el registro: *Configuración → Prueba → Ver registro*. |
| Pruebas rápidas | `Test-NetConnection <IP> -Port 47810` desde PowerShell en la otra PC. |

El registro (`%LOCALAPPDATA%\Susurro\logs\susurro.log`, máx. ~512 KB con rotación) anota inicio,
conexión, desconexión, errores, vinculación y envío/recepción de mensajes (sin su contenido).

## 12. Dos instancias en la misma PC (desarrollo)

Cada **perfil** tiene su configuración, `InstanceId`, vínculo, registro e instancia única propios:

```bash
powershell -ExecutionPolicy Bypass -File scripts/run-two-instances.ps1
```

Equivale a:

```bash
Susurro.exe --profile A --port 47820
```

```bash
Susurro.exe --profile B --port 47830
```

Ambas comparten el UDP 47811, así que se descubren solas. Vinculalas como en la sección 6
(o con la dirección `127.0.0.1:47820`). `-Reset` borra los perfiles para empezar de cero.
Los perfiles de desarrollo no se agregan al inicio de Windows.

---

## Uso

**Atajo global**: <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Espacio</kbd> abre Susurro desde cualquier programa con el
cursor en el campo de texto. <kbd>Enter</kbd> envía, la ventana se oculta y el foco vuelve a la aplicación
en la que estabas (si no hay conexión, queda visible mostrando "En espera"). <kbd>Esc</kbd> o volver a
apretar el atajo cancela. Se cambia o desactiva en *Configuración → General → Atajo de teclado*: hacé
clic en el campo y apretá la combinación; si Windows u otro programa ya la usa, Susurro avisa
(por ejemplo, <kbd>Win</kbd>+<kbd>Espacio</kbd> está reservado por Windows para cambiar el idioma del teclado).
Usa `RegisterHotKey`: no hay ganchos de teclado ni se lee ninguna otra tecla.

**Bandeja del sistema**: clic izquierdo muestra/oculta la ventana; clic derecho abre el menú
*Mostrar ventana · Ocultar ventana · Configuración · Salir*. El punto del icono indica el estado
(verde conectado, ámbar conectando, gris desconectado). Cerrar la ventana la oculta en la bandeja.

**Línea de comandos**

| Argumento | Efecto |
|---|---|
| `--minimized` | Arranca oculto en la bandeja |
| `--autostart` | Lo usa el inicio con Windows (oculto en la bandeja) |
| `--profile NOMBRE` | Perfil separado (pruebas) |
| `--port N` / `--discovery-port N` | Cambia y guarda los puertos |
| `--set-autostart on\|off` | Activa/desactiva el inicio con Windows y sale (instalador) |
| `--uninstall-cleanup` | Quita el inicio con Windows y sale (desinstalador) |

## El overlay

- Ventana Win32 creada con `HwndSource` y estilos `WS_EX_NOACTIVATE | WS_EX_TRANSPARENT |
  WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`, mostrada con `SW_SHOWNOACTIVATE`:
  - **nunca roba el foco** (seguís escribiendo en Word, el navegador, Visual Studio…);
  - **los clics lo atraviesan**;
  - **no aparece en Alt+Tab ni en la barra de tareas**;
  - no captura teclado ni mouse, no minimiza ni toca la aplicación activa.
- Un mensaje a la vez. Los que llegan mientras hay uno visible esperan en una cola (máx. 20); los
  urgentes pasan delante de los normales pendientes, sin interrumpir al actual.
- Duración configurable (2, 5, 8 o 10 s) + hasta 4 s extra de lectura para textos largos.
- Animación de entrada (200 ms) y salida (320 ms) muy sutiles; se desactivan si lo pedís o si
  Windows tiene las animaciones apagadas.
- **Multimonitor**: *Automático* (el monitor de la ventana en uso), *Monitor principal* o un monitor
  concreto. Si ese monitor se desconecta, usa el principal.
- **DPI por monitor (PerMonitorV2)**: la ventana se crea directamente en el monitor destino y se
  mide y ubica en píxeles físicos con su DPI (100/125/150/200 %). Si el texto no entra (monitor chico
  + fuente grande), reduce la fuente hasta que entre: nunca se corta ni sale de la pantalla.
- Se destruye al terminar cada mensaje: en reposo no hay ninguna ventana ni superficie en memoria.

## Configuración

| Pestaña | Opciones |
|---|---|
| **General** | Nombre de esta PC · Iniciar con Windows · Iniciar minimizado · Icono en la bandeja · **Atajo de teclado** · Confirmar recepción |
| **Overlay** | Monitor · Posición (7 opciones) · Distancia al borde · Ancho máximo · Duración · Animaciones · Probar |
| **Apariencia** | Vista previa en vivo · **Color del texto** (blanco, amarillo, gris claro) · Tamaño · **Contorno de las letras** · Nombre del remitente · **Recuadro de fondo** (on/off) · Opacidad · Estilo de urgentes · Alto contraste |
| **Conexión** | PC vinculada · Estado · Reconectar · Volver a vincular · Desvincular · Dirección manual · IP local · Puerto |
| **Prueba** | Mostrar mensaje de prueba (solo en esta PC, con la configuración sin guardar) · Ver registro · Carpeta de datos |

Sin recuadro de fondo, el texto flota como un subtítulo de película; el contorno oscuro se
activa solo para que se lea sobre cualquier fondo.

Archivos (todo en `%LOCALAPPDATA%\Susurro\`): `settings.json` (unos pocos KB) y `logs\`. Nada más.

## Rendimiento

Publicación Release, en la bandeja del sistema, sin ventanas abiertas:

| Métrica | Valor |
|---|---|
| CPU en reposo | **0 ms de CPU en 30 s** (0,000 %) |
| Memoria residente (working set) | **~9 MB** tras el recorte de memoria |
| Hilos | 15 |
| Red en reposo, conectado | un `ping` + `pong` cada 30 s de inactividad (< 200 bytes) |
| Red con la otra PC apagada | ≤ 1 intento TCP + 3 datagramas UDP por minuto |
| Disco | ~140 MB el programa autocontenido; configuración de pocos KB; log ≤ ~512 KB |

Por qué: sin sondeo ni bucles (lectura asíncrona, temporizadores de un disparo), GC de estación
de trabajo sin hilo concurrente, JSON con generación de código, overlay y ventanas que se destruyen
al cerrarse, recorte de memoria solo tras eventos (no periódico), icono de bandeja nativo sin WinForms.

## Tests

```bash
dotnet test tests/Susurro.Core.Tests
```

80 tests (xUnit):

- **Protocolo y serialización**: ida y vuelta de paquetes, JSON inválido, campos desconocidos,
  tramas (tamaño máximo, truncadas, EOF), AES-GCM (manipulación, repetición, reordenamiento, otra clave).
- **Pairing**: códigos (generación, normalización), ECDH/PBKDF2/HKDF, código incorrecto, MITM,
  expiración e intentos, DPAPI.
- **Validación y cola**: limpieza de texto, límites, duplicados, orden, urgentes, capacidad.
- **Reconexión**: backoff, arbitraje de conexiones duplicadas.
- **Configuración**: valores por defecto, persistencia, archivo corrupto, límites, clon profundo.
- **Integración con sockets reales**: vincular + mensajes con Entregado/Visto, código incorrecto,
  código invalidado por intentos, reconexión tras reinicio con entrega de mensajes en espera,
  reconexión con otro puerto, suplantación rechazada, basura en el puerto, desvincular, descubrimiento UDP.

## Decisiones técnicas

| Tema | Decisión | Motivo |
|---|---|---|
| Transporte | TCP con tramas por longitud | Más liviano y simple que WebSocket; ver [PROTOCOL.md](docs/PROTOCOL.md) |
| Topología | P2P simétrico, ambos marcan | Robusto si el firewall bloquea en un solo sentido; arbitraje determinista |
| Seguridad | ECDH + código (PBKDF2) → clave de vínculo; HMAC mutuo + AES-GCM por sesión | Sin contraseñas, sin confiar en la IP, mensajes privados en la LAN |
| Clave en disco | DPAPI (usuario actual) | No sirve si se copia el archivo |
| Overlay | `HwndSource` en vez de `Window` | Control exacto de estilos Win32 y DPI del monitor destino |
| Bandeja | `Shell_NotifyIcon` propio | Sin cargar WinForms; reinstala el icono si el Explorador se reinicia |
| Inicio con Windows | `HKCU\...\Run` | Por usuario, sin administrador, sin servicios ni tareas programadas |
| Instalador | Inno Setup (solo al compilar) | Instalador estándar y pequeño; alternativa portátil sin dependencias |
| Publicación | single-file autocontenido, sin comprimir, sin trimming | Comprimir sube la RAM al arrancar; WPF no admite trimming |
| Mensajes en espera | 2 min, máx. 20, en memoria | Un "susurro" viejo no tiene sentido; nada se escribe a disco |
