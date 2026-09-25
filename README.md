<div align="center">

<img src="docs/assets/banner.svg" alt="Susurro, para evitar los gritos en la oficina" width="100%">

# Susurro, para evitar los gritos en la oficina

<br>

![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-1c1d21?style=flat-square&logo=windows&logoColor=8aa9d6)
![.NET 8](https://img.shields.io/badge/.NET-8-1c1d21?style=flat-square&logo=dotnet&logoColor=8aa9d6)
![WPF](https://img.shields.io/badge/UI-WPF-1c1d21?style=flat-square)
![LAN P2P](https://img.shields.io/badge/red-LAN%20P2P-1c1d21?style=flat-square)
![Cifrado](https://img.shields.io/badge/cifrado-AES--256--GCM-1c1d21?style=flat-square)
![Tests](https://img.shields.io/badge/tests-123%20OK-6fbf8e?style=flat-square)
[![CI](https://github.com/TintoUriel/Susurro/actions/workflows/ci.yml/badge.svg)](https://github.com/TintoUriel/Susurro/actions/workflows/ci.yml)

**Mensajes breves, imágenes y archivos entre las PCs de la oficina, como un subtítulo discreto.**<br>
Sin servidor · sin nube · sin cuentas · sin códigos · sin sonidos · sin robar el foco

<a href="https://github.com/TintoUriel/Susurro/releases/latest"><img src="https://img.shields.io/badge/Descargar-Susurro.exe-8aa9d6?style=for-the-badge&logo=windows&logoColor=101216&labelColor=e7e8ea" alt="Descargar Susurro.exe"></a>

[Características](#características) · [Capturas](#capturas) · [Inicio rápido](#inicio-rápido) · [Documentación](#índice)

</div>

---

<p align="center">
  <img src="docs/assets/overlay.png" alt="Un mensaje de Susurro mostrado como subtítulo sobre el escritorio" width="820">
</p>

Elegís a quién (o a todos los conectados), escribís y, en su PC, el mensaje aparece unos segundos
**por encima de todo**, como un subtítulo. Quien lo recibe puede seguir escribiendo en Word, en el navegador o en Visual Studio:
Susurro **no toma el foco, no captura el mouse ni el teclado, no aparece en Alt+Tab** y los clics lo atraviesan. Los mensajes **importantes** son la excepción: quedan en
pantalla hasta que les hacés clic (igual sin quitarle el foco a lo que estés usando).

<table>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/assets/ventana.png" alt="Ventana principal de Susurro" width="100%">
    </td>
    <td width="50%" valign="top">

**Una utilidad pequeña, no una aplicación empresarial.**

- <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Espacio</kbd> desde **cualquier programa** abre Susurro listo para escribir
- **Para:** una persona o todos los conectados · <kbd>Ctrl</kbd>+<kbd>↑</kbd>/<kbd>↓</kbd> cambia mientras escribís
- <kbd>Enter</kbd> envía y te devuelve a lo que estabas · <kbd>Ctrl</kbd>+<kbd>I</kbd> importante · <kbd>Esc</kbd> oculta
- <kbd>Ctrl</kbd>+<kbd>V</kbd> pega una **imagen** o **archivos** copiados · 📎 o arrastrar para adjuntar
- Ves cuándo alguien **te está escribiendo**
- Máximo 300 caracteres; el campo se limpia y conserva el foco
- `Enviado` → `Entregado ✓` → `Visto ✓`
- <kbd>F1</kbd> (o ⌨ en la ventana) muestra todos los atajos
- Vive en la bandeja del sistema, arranca con Windows y **se actualiza sola**
- No guarda historial de mensajes

</td>
  </tr>
</table>

## Características

| | |
|---|---|
| ⌨️ **Atajo global** | <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Espacio</kbd> desde cualquier programa: escribís, <kbd>Enter</kbd>, y volvés a lo que estabas. Configurable. <kbd>F1</kbd> muestra todos los atajos. |
| 🖼️ **Imágenes con Ctrl+V** | Copiás una captura o una imagen y la pegás en el mensaje: le aparece a la otra persona en el subtítulo, con tu comentario, hasta que la cierra o la **guarda** en Descargas. |
| 📎 **Archivos** | Pegalos, arrastralos o elegilos con 📎. A la otra persona le aparece una tarjeta **arriba a la izquierda** con **Descargar** o **Cerrar**; viajan recién cuando elige descargar, con progreso, y quedan en su carpeta Descargas. |
| ✍️ **Está escribiendo…** | Al lado de "1 persona conectada" ves quién te está escribiendo en ese momento. |
| 🪶 **Liviano de verdad** | 0 % de CPU en reposo (sin sondeo ni bucles), ~9 MB residentes en la bandeja, configuración de pocos KB. |
| 👥 **Toda la oficina** | Cada persona con Susurro en la red aparece sola en la lista. Le escribís a una o a todos los conectados. |
| 🙋 **Tu nombre, no el de la PC** | La primera vez te pregunta cómo te llamás; así te ven los demás. Sin códigos ni vinculación. |
| 🔌 **Directo por la LAN** | Las PCs se hablan entre sí por TCP. Sin servidor central, sin base de datos: los mensajes nunca pasan por internet. |
| 🔄 **Se actualiza sola** | Una vez por día busca la última versión en GitHub, la verifica (SHA-256) y la instala sin avisos ni ventanas: se reinicia en un momento en que no estás usando la PC. Si la versión nueva no arranca, vuelve sola a la anterior. |
| 🔎 **Se encuentran solas** | Descubrimiento automático por UDP; si cambia la IP, se vuelven a encontrar por su identificador. |
| 🔁 **Reconexión automática** | PC apagada, reiniciada, suspendida o red caída: se reconecta sola y entrega lo que quedó en espera. |
| 🔐 **Seguro sin contraseñas** | Cada PC tiene una identidad de clave pública (su id es el hash de la clave): nadie puede hacerse pasar por otra. Autenticación mutua y AES-256-GCM. Podés bloquear a quien quieras. |
| 🎬 **Overlay tipo subtítulo** | Posición, monitor, duración, tamaño, color (blanco / amarillo / gris), recuadro opcional y contorno de letras. |
| 📌 **Importantes** | Quedan en pantalla hasta que les hacés clic, sin robar el foco; el *Visto* le llega a quien lo mandó recién en ese momento. Sin sonidos ni ventanas emergentes. |
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
      <img src="docs/assets/overlay.png" alt="Mensaje normal como subtítulo" width="100%"><br>
      <sub><b>Mensaje</b> — aparece unos segundos y se va solo</sub>
    </td>
  </tr>
  <tr>
    <td align="center">
      <img src="docs/assets/imagen.png" alt="Imagen recibida en el subtítulo, con Guardar y Cerrar" width="100%"><br>
      <sub><b>Imagen</b> — pegada con Ctrl+V; se guarda o se cierra</sub>
    </td>
    <td align="center">
      <img src="docs/assets/archivo.png" alt="Tarjeta de archivo recibido arriba a la izquierda" width="92%"><br>
      <sub><b>Archivo</b> — aparece arriba a la izquierda: Descargar o Cerrar</sub><br><br>
      <img src="docs/assets/adjuntar.png" alt="Imagen adjunta con un comentario" width="70%"><br>
      <sub><b>Adjuntar</b> — Ctrl+V, 📎 o arrastrar</sub>
    </td>
  </tr>
  <tr>
    <td align="center">
      <img src="docs/assets/escribiendo.png" alt="Ana está escribiendo…" width="70%"><br>
      <sub><b>Está escribiendo…</b> al lado del estado</sub>
    </td>
    <td align="center">
      <img src="docs/assets/importante.png" alt="Mensaje importante" width="100%"><br>
      <sub><b>Importante</b> — queda hasta que le hacés clic</sub>
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
      <img src="docs/assets/personas.png" alt="Lista de personas en la red, con bloqueo y agregar por dirección" width="92%"><br>
      <sub><b>Personas</b> — se encuentran solas; bloquear o agregar por dirección</sub>
    </td>
    <td valign="middle">

### Inicio rápido

1. Descargá [**Susurro.exe**](https://github.com/TintoUriel/Susurro/releases/latest) y abrilo en **cada PC**.
2. Escribí tu nombre y tocá **Empezar**.
3. Listo: tus compañeros aparecen solos en **Para:**. Sin códigos.

<img src="docs/assets/bienvenida.png" alt="Bienvenida: ¿Cómo te llamás?" width="92%">

Detalles en [Cómo instalar](#5-cómo-instalar) y [Cómo empezar](#6-cómo-empezar-tu-nombre-y-tus-compañeros).

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
6. [Cómo empezar: tu nombre y tus compañeros](#6-cómo-empezar-tu-nombre-y-tus-compañeros)
7. [Firewall de Windows](#7-firewall-de-windows)
8. [Cómo funciona el descubrimiento](#8-cómo-funciona-el-descubrimiento)
9. [Cómo funciona la reconexión](#9-cómo-funciona-la-reconexión)
10. [Cómo cambiar el puerto](#10-cómo-cambiar-el-puerto)
11. [Solución de problemas de conexión](#11-solución-de-problemas-de-conexión)
12. [Dos instancias en la misma PC (desarrollo)](#12-dos-instancias-en-la-misma-pc-desarrollo)

Anexos: [Uso](#uso) · [Actualizaciones automáticas](#actualizaciones-automáticas) · [Overlay](#el-overlay) · [Configuración](#configuración) · [Rendimiento](#rendimiento)
· [Tests](#tests) · [Decisiones técnicas](#decisiones-técnicas) · [Protocolo](docs/PROTOCOL.md) · [Identidad y seguridad](docs/IDENTIDAD.md)

---

## 2. Arquitectura

```
Susurro.sln
├─ src/Susurro.Core          (net8.0, sin UI — probado con tests)
│  ├─ Config/       AppSettings, SettingsValidator, SettingsStore (JSON atómico)
│  ├─ Logging/      Log + FileLogSink (256 KB con rotación)
│  ├─ Protocol/     Packet, FrameIO, SecureChannel (AES-GCM), HandshakeCrypto, JSON con source-gen
│  ├─ Identity/     LocalIdentity (clave ECDH P-256, id = hash de la clave, clave de enlace), DPAPI
│  ├─ Discovery/    DiscoveryService (UDP multicast + broadcast)
│  ├─ Net/          PeerLink (contactos, conexiones, reconexión, bandeja por destinatario), PeerSession, SessionArbiter, Backoff
│  └─ Messaging/    MessageRules, DuplicateFilter, DisplayQueue, WhisperMessage
├─ src/Susurro.App           (net8.0-windows, WPF)
│  ├─ AppController.cs  une todo; pasa eventos de red al hilo de UI
│  ├─ Overlay/      OverlayHost (HwndSource Win32), SubtitleVisual, OverlayController, MonitorService
│  ├─ Tray/         TrayIcon (Shell_NotifyIcon nativo, sin WinForms)
│  ├─ Views/        MainWindow, SettingsWindow, WelcomeWindow, LogWindow
│  ├─ Services/     AutoStart (HKCU\Run), SingleInstance, CommandLine, WindowStyling, MemoryTrimmer
│  └─ Themes/Dark.xaml  todo el estilo propio (sin frameworks visuales)
├─ tests/Susurro.Core.Tests  (xUnit)
├─ installer/Susurro.iss     (Inno Setup → SusurroSetup.exe)
├─ scripts/                  build, dos instancias, instalación portátil, firewall, icono
└─ docs/                     PROTOCOL.md, IDENTIDAD.md
```

Flujo de un mensaje:

```
MainWindow ─Send(para)→ AppController ─→ PeerLink.Send (una copia por destinatario) ─→ bandeja de salida ─→ PeerSession (AES-GCM/TCP)
                                                                               │ LAN
PeerSession ─→ PeerLink (valida, descarta duplicados, ack) ─→ AppController ─→ OverlayController
                                                                      └─→ OverlayHost (subtítulo)
```

`Susurro.Core` no depende de WPF: toda la lógica de red, protocolo, identidad, contactos, cola y
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

**Con GitHub Actions** ([.github/workflows/ci.yml](.github/workflows/ci.yml)): cada push y cada pull request
corre los tests en Windows, publica `Susurro.exe` y arma el instalador. Para sacar una versión:

1. Subí `<Version>` en [Directory.Build.props](Directory.Build.props) (por ejemplo `2.3.0`) y hacé push.
2. Creá el tag con la misma versión: `git tag v2.3.0 && git push origin v2.3.0`.
3. El CI crea la *release* con `Susurro.exe`, `SusurroSetup.exe` y `susurro-update.json` (versión,
   dirección, tamaño y SHA-256 del `.exe`). Desde ese momento, todas las PCs se actualizan solas
   en menos de un día (ver [Actualizaciones automáticas](#actualizaciones-automáticas)).

Si el tag no coincide con `Directory.Build.props`, el CI falla antes de publicar nada.

> Alternativa mínima: si las PCs ya tienen el *.NET 8 Desktop Runtime*, se puede publicar
> dependiente del framework (~1 MB):
> `dotnet publish src/Susurro.App -c Release -r win-x64 --self-contained false -o artifacts/fdd`

## 5. Cómo instalar

**La forma más simple — un solo `.exe`**: descargá `Susurro.exe` desde
[Releases](https://github.com/TintoUriel/Susurro/releases/latest), guardalo en una carpeta fija
(por ejemplo `C:\Programas\Susurro\`) y abrilo. No necesita instalar nada más: la primera vez
pregunta tu nombre y queda configurado para iniciar con Windows.

> Windows puede mostrar *"Windows protegió tu PC"* porque el ejecutable no está firmado
> digitalmente: **Más información → Ejecutar de todas formas**. Y la primera vez, el aviso del
> Firewall: **Permitir** (ver [sección 7](#7-firewall-de-windows)).

**Opción A — instalador** (`artifacts/installer/SusurroSetup.exe`, generado por `scripts/build.ps1`
si tenés [Inno Setup 6](https://jrsoftware.org/isdl.php)):

- Instala en *Archivos de programa*, crea el acceso directo del menú Inicio (y opcional el de escritorio).
- Deja la carpeta del programa con permiso de escritura para los usuarios, así Susurro se
  [actualiza solo](#actualizaciones-automáticas) sin pedir permisos de administrador.
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

## 6. Cómo empezar: tu nombre y tus compañeros

1. La primera vez que se abre Susurro pregunta **¿Cómo te llamás?** Ese es el nombre que ven los
   demás (se cambia en *Configuración → General → Tu nombre*). Hasta elegirlo, Susurro no sale a la red.
2. Todas las PCs con Susurro abierto en la misma red se encuentran solas en segundos y aparecen en
   **Para:** de la ventana principal, con un punto verde si están conectadas.
3. Elegí a una persona o **Todos los conectados**, escribí y <kbd>Enter</kbd>. Susurro recuerda a
   quién le escribiste la última vez.

No hay códigos ni vinculación. En *Configuración → Personas* está la lista completa, y desde ahí se
puede **bloquear** a alguien (no puede conectarse ni enviarte mensajes), **quitar** PCs que ya no se
usan o **agregar por dirección** a alguien que no aparece solo.

Cómo se evita que alguien se haga pasar por otro, y qué no cubre: [docs/IDENTIDAD.md](docs/IDENTIDAD.md).

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
  - un *anuncio* al iniciar, al cambiar la red, al volver de suspensión o al cambiar tu nombre;
  - una *consulta* (3 datagramas en 1,5 s) al iniciar, al cambiar la red, con *Buscar de nuevo* o
    cuando hay un mensaje esperando a alguien que no aparece.
- Lo único que sale de la LAN es la [búsqueda de actualizaciones](#actualizaciones-automáticas): una
  consulta HTTPS a GitHub por día (se desactiva en *Configuración → General*).
- Las respuestas incluyen `InstanceId`, nombre y puerto TCP. Esa información **no es confiable**:
  solo aporta direcciones candidatas; la identidad se verifica criptográficamente al conectar.
- Una PC nueva se agrega a la lista recién después de verificar su identidad por TCP.
- Si el descubrimiento no funciona en tu red (VLAN distintas, multicast bloqueado), agregá a la
  persona con su IP o nombre de equipo en *Configuración → Personas → Agregar por dirección*.

## 9. Cómo funciona la reconexión

- Cualquiera de las dos PCs de cada par puede iniciar la conexión; si ambas lo hacen a la vez, una
  regla determinista elige la misma en los dos lados.
- Cada compañero tiene su propio intento de conexión, **dormido** hasta que haya un motivo: arranque
  (un intento con su última dirección), su *anuncio* al encenderse, un cambio de red o la vuelta de
  suspensión, un mensaje esperando para esa persona o *Buscar de nuevo*.
- Si se pierde una conexión sin aviso, o hay mensajes esperando, se reintenta con esperas crecientes
  **1, 2, 5, 10, 20, 30, 60 s**. Si la otra PC avisó que se cerraba, no se insiste: vuelve a
  aparecer sola cuando se enciende.
- **Cambio de IP**: la identidad es el `InstanceId`, no la IP; el descubrimiento encuentra la nueva
  dirección y la guarda como "última conocida".
- **Detección de caída**: `bye` inmediato si la otra PC cierra Susurro o Windows; latido solo si
  hay 30 s sin tráfico; conexión muerta tras 75 s sin respuesta.
- Los mensajes enviados sin conexión esperan hasta 2 minutos y se entregan al reconectar.

Detalle completo: [docs/PROTOCOL.md](docs/PROTOCOL.md).

## 10. Cómo cambiar el puerto

*Configuración → Personas → Puerto TCP* → Guardar. La comunicación se reinicia sola.

- Cada PC puede usar su propio puerto: las demás lo aprenden al conectarse o por descubrimiento.
- Si el descubrimiento no funciona y cambiaste el puerto, agregala por dirección con `IP:puerto`.
- La regla de firewall del instalador es **por programa**, así que sigue valiendo con otro puerto.
- Desde la línea de comandos: `Susurro.exe --port 50000` (se guarda). El UDP de descubrimiento se
  cambia con `--discovery-port` (debe ser el mismo en ambas PCs).

## 11. Solución de problemas de conexión

| Síntoma | Qué hacer |
|---|---|
| **Nadie aparece** en *Para:* | ¿Está Susurro abierto en las otras PCs? ¿Están en la misma red? Probá *Configuración → Personas → Buscar de nuevo*. |
| Alguien aparece **desconectado** | Se reconecta solo cuando abre Susurro. Si sigue así, revisá su firewall ([sección 7](#7-firewall-de-windows)). |
| "Responde, pero el puerto TCP no es accesible (¿firewall?)" | Falta la regla de firewall en esa PC ([sección 7](#7-firewall-de-windows)). |
| "No acepta conexiones de esta PC" | Esa persona te bloqueó (o te quitó y bloqueó). |
| "El puerto 47810 está en uso por otro programa" | Cambiá el puerto ([sección 10](#10-cómo-cambiar-el-puerto)). |
| Alguien no aparece nunca (redes separadas) | Multicast/broadcast bloqueado: agregalo por dirección (su IP está en *Configuración → Personas → Esta PC*). |
| "La otra PC tiene una versión incompatible" | Actualizá Susurro en todas las PCs: la versión con códigos no se conecta con esta. |
| Se conecta y se desconecta | Revisá el registro: *Configuración → Prueba → Ver registro*. |
| Pruebas rápidas | `Test-NetConnection <IP> -Port 47810` desde PowerShell en la otra PC. |

El registro (`%LOCALAPPDATA%\Susurro\logs\susurro.log`, máx. ~512 KB con rotación) anota inicio,
contactos nuevos, conexión, desconexión, errores y envío/recepción de mensajes (sin su contenido).

## 12. Dos instancias en la misma PC (desarrollo)

Cada **perfil** tiene su configuración, identidad, contactos, registro e instancia única propios:

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

Ambas comparten el UDP 47811, así que se encuentran solas en cuanto cada una tiene nombre. Para
simular una oficina se pueden abrir más perfiles (`--profile C --port 47840`, …).
`-Reset` borra los perfiles para empezar de cero.
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

**Para quién**: el selector **Para:** lista a tus compañeros (● conectado, ○ desconectado) y, si hay
más de uno, **Todos los conectados**. <kbd>Ctrl</kbd>+<kbd>↑</kbd>/<kbd>↓</kbd> cambia de destinatario sin
sacar las manos del texto. Si la persona está desconectada, el mensaje espera hasta 2 minutos. Al enviar
a todos, el estado se resume: *Entregado a 3 de 4*, *Visto por todos ✓*.

**Imágenes**: copiá una imagen (una captura con <kbd>Win</kbd>+<kbd>Shift</kbd>+<kbd>S</kbd>, "Copiar imagen"
en el navegador…) y apretá <kbd>Ctrl</kbd>+<kbd>V</kbd> en el campo de mensaje: aparece adjunta con su
miniatura y lo que escribas va como comentario. A la otra persona le aparece en el subtítulo con
**Guardar** (a su carpeta Descargas) y **Cerrar**; queda hasta que la cierra, y ahí te llega *Vista ✓*.
Hasta 10 MB (las más grandes se comprimen solas). Solo a quien está conectado.

**Archivos**: pegalos con <kbd>Ctrl</kbd>+<kbd>V</kbd> (copiados en el Explorador), arrastralos a la ventana
o elegilos con 📎 (hasta 4 GB, hasta 10 por envío). A la otra persona le aparece una tarjeta **arriba
a la izquierda** de la pantalla —que tampoco le roba el foco— con quién lo manda, nombre y tamaño,
y **Descargar** / **Cerrar**. El archivo viaja recién cuando elige descargarlo (con barra de progreso),
cifrado como los mensajes, y se verifica con SHA-256; se guarda en su carpeta **Descargas** (con otro
nombre si ya existe) y después puede **Abrir** o **Mostrar en carpeta**. Vos ves *Archivo ofrecido ✓* →
*Enviando… 45 %* → *Descargado ✓* (o *Lo cerró sin descargar*). Por seguridad, el nombre recibido se
limpia (sin rutas ni nombres reservados) y el archivo queda marcado como descargado de otra PC, así
Windows avisa antes de ejecutar un programa. La oferta dura 30 minutos.

**Está escribiendo…**: mientras alguien te escribe (a vos o a todos los conectados), al lado de
"1 persona conectada" aparece *· Ana está escribiendo…*. El aviso se manda al teclear (como mucho
cada 3 s), se borra solo a los 6 s sin teclas nuevas y al llegar el mensaje: no hay tráfico periódico.

**Bandeja del sistema**: clic izquierdo muestra/oculta la ventana; clic derecho abre el menú
*Mostrar ventana · Ocultar ventana · Configuración · Atajos de teclado · Salir*. El punto del icono es verde si hay
alguien conectado y gris si no; al pasar el mouse dice cuántas personas. Cerrar la ventana la oculta en la bandeja.

**Línea de comandos**

| Argumento | Efecto |
|---|---|
| `--minimized` | Arranca oculto en la bandeja |
| `--autostart` | Lo usa el inicio con Windows (oculto en la bandeja) |
| `--profile NOMBRE` | Perfil separado (pruebas) |
| `--port N` / `--discovery-port N` | Cambia y guarda los puertos |
| `--set-autostart on\|off` | Activa/desactiva el inicio con Windows y sale (instalador) |
| `--uninstall-cleanup` | Quita el inicio con Windows y sale (desinstalador) |
| `--updated PID` | Lo usa la actualización automática al lanzar la versión nueva |

**Atajos de teclado**: <kbd>F1</kbd> en la ventana principal (o el botón ⌨ de la barra de título, o
*Atajos de teclado* en el menú de la bandeja) abre una ventana con todos los atajos, incluido el atajo
global que tengas configurado.

## Actualizaciones automáticas

Susurro se mantiene al día solo, sin que nadie tenga que hacer nada:

1. Un rato después de arrancar (entre 2 y 10 minutos, al azar para que no consulten todas las PCs a
   la vez) y después **una vez por día**, pide `susurro-update.json` a la última release de GitHub.
   Es un temporizador de un disparo: no hay sondeo ni CPU en reposo.
2. Si hay una versión más nueva, descarga `Susurro.exe` junto al actual (`Susurro.exe.download`),
   solo desde `github.com` por HTTPS, y verifica el **tamaño y el SHA-256** del manifiesto. Si algo
   no coincide, lo borra y no toca nada.
3. Cambia los archivos de lugar: el ejecutable en uso pasa a `Susurro.exe.old` (Windows permite
   renombrar un `.exe` abierto) y el nuevo toma su nombre. La ruta no cambia, así que la regla de
   firewall y el inicio con Windows siguen valiendo.
4. Espera un momento **en que no se note**: sin ventanas abiertas, nada en pantalla, ningún mensaje
   en espera ni archivo transfiriéndose, nadie escribiéndote y al menos 2 minutos sin tocar teclado
   ni mouse. Ahí lanza la versión nueva (oculta en la bandeja) y se cierra. Tus compañeros la ven
   desconectada un segundo y los mensajes que te manden en ese momento te llegan igual.
5. Si la versión nueva no avisa que arrancó en 60 s, la detiene, vuelve a la anterior y no la
   reintenta hasta el próximo inicio. Si nunca hubo un buen momento, la versión nueva se usa al
   próximo inicio de Windows.

Requisitos: que la carpeta del programa admita escritura (el instalador 2.2.0 o posterior la deja
así; `%LOCALAPPDATA%\Programs\Susurro` o cualquier carpeta tuya ya la admite). Las instalaciones
anteriores a la 2.2.0 hay que actualizarlas **una vez** a mano. No se actualizan solos los perfiles
de desarrollo (`--profile`) ni las compilaciones Debug. Se desactiva en *Configuración → General →
Actualizar automáticamente*; el registro (`update`) cuenta cada búsqueda.

## El overlay

- Ventana Win32 creada con `HwndSource` y estilos `WS_EX_NOACTIVATE | WS_EX_TRANSPARENT |
  WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`, mostrada con `SW_SHOWNOACTIVATE`:
  - **nunca roba el foco** (seguís escribiendo en Word, el navegador, Visual Studio…);
  - **los clics lo atraviesan** (salvo en los importantes, ver abajo);
  - **no aparece en Alt+Tab ni en la barra de tareas**;
  - no captura teclado ni mouse, no minimiza ni toca la aplicación activa.
- Un mensaje a la vez. Los que llegan mientras hay uno visible esperan en una cola (máx. 20); los
  importantes pasan delante de los normales pendientes, sin interrumpir al actual.
- Duración configurable (2, 5, **8** —predeterminado— o 10 s) + hasta 4 s extra de lectura para textos largos.
- **Imágenes**: la imagen (hasta el 45 % del alto de la pantalla) con el comentario debajo y los botones
  *Guardar* / *Cerrar*. Como los importantes, recibe clics sin activarse y queda hasta que se cierra.
  Antes de decodificarla se valida que sea PNG/JPEG y que sus dimensiones sean razonables.
- **Mensajes importantes** (botón *Importante* o <kbd>Ctrl</kbd>+<kbd>I</kbd> al escribir): no se van
  solos, quedan en pantalla con la leyenda *Clic para cerrar* hasta que se les hace clic. Esa ventana
  recibe el clic pero sigue sin activarse (`WS_EX_NOACTIVATE` + `MA_NOACTIVATE`): el foco no se mueve
  de la aplicación en uso. Con *Confirmar recepción*, quien lo mandó ve *Visto ✓* recién al clic.
  Mientras un importante espera, los mensajes que llegan quedan en la cola.
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
| **General** | Tu nombre · Iniciar con Windows · Iniciar minimizado · Icono en la bandeja · **Atajo de teclado** (y *Ver todos los atajos*) · Confirmar recepción · **Actualizar automáticamente** |
| **Overlay** | Monitor · Posición (7 opciones) · Distancia al borde · Ancho máximo · Duración · Animaciones · Probar |
| **Apariencia** | Vista previa en vivo · **Color del texto** (blanco, amarillo, gris claro) · Tamaño · **Contorno de las letras** · Nombre del remitente · **Recuadro de fondo** (on/off) · Opacidad · Estilo de importantes · Alto contraste |
| **Personas** | Lista con estado e IP · Buscar de nuevo · Bloquear / Desbloquear · Quitar de la lista · Agregar por dirección · IP local · Puerto |
| **Prueba** | Mostrar mensaje de prueba (solo en esta PC, con la configuración sin guardar) · Ver registro · Carpeta de datos |

Sin recuadro de fondo, el texto flota como un subtítulo de película; el contorno oscuro se
activa solo para que se lea sobre cualquier fondo.

Archivos (todo en `%LOCALAPPDATA%\Susurro\`): `settings.json` (unos pocos KB) y `logs\`. Las descargas en
curso se escriben en `incoming\` y al terminar se mueven a Descargas (nunca queda un archivo a medias).

## Rendimiento

Publicación Release, en la bandeja del sistema, sin ventanas abiertas:

| Métrica | Valor |
|---|---|
| CPU en reposo | **0 ms de CPU en 30 s** (0,000 %) |
| Memoria residente (working set) | **~9 MB** tras el recorte de memoria |
| Hilos | 15 |
| Red en reposo, conectado | un `ping` + `pong` cada 30 s de inactividad por compañero (< 200 bytes) |
| Red con compañeros apagados | nada: se reconectan cuando la otra PC se anuncia al encenderse |
| Internet | una consulta HTTPS a GitHub por día (~1 KB); la descarga (~140 MB) solo cuando hay versión nueva |
| Disco | ~140 MB el programa autocontenido; configuración de pocos KB; log ≤ ~512 KB |

Por qué: sin sondeo ni bucles (lectura asíncrona, temporizadores de un disparo), GC de estación
de trabajo sin hilo concurrente, JSON con generación de código, overlay y ventanas que se destruyen
al cerrarse, recorte de memoria solo tras eventos (no periódico), icono de bandeja nativo sin WinForms.

## Tests

```bash
dotnet test tests/Susurro.Core.Tests
```

123 tests (xUnit), que el [CI](.github/workflows/ci.yml) corre en Windows en cada push:

- **Protocolo y serialización**: ida y vuelta de paquetes, JSON inválido, campos desconocidos,
  tramas (tamaño máximo, truncadas, EOF), AES-GCM (manipulación, repetición, reordenamiento, otra clave).
- **Identidad**: id = hash de la clave pública, misma clave de enlace en ambos lados y distinta para
  un tercero, claves inválidas, identidad guardada/ilegible, DPAPI.
- **Validación y cola**: limpieza de texto, límites, duplicados, orden, importantes, capacidad.
- **Reconexión**: backoff, arbitraje de conexiones duplicadas.
- **Configuración**: valores por defecto (sin nombre del equipo), persistencia, contactos inválidos
  o repetidos, límite de contactos, migración desde la versión con códigos, archivo corrupto, clon profundo.
- **Integración con sockets reales**: desconocidos que se conectan sin código con Entregado/Visto,
  tres personas con mensajes solo al elegido, cambio de nombre, reconexión tras reinicio con entrega
  de mensajes en espera, reconexión con otro puerto, suplantación de id rechazada, versión vieja
  rechazada, bloquear/desbloquear, quitar contacto, basura en el puerto, descubrimiento UDP.
- **Imágenes y archivos**: imagen completa con comentario y Entregada/Vista, archivo que solo viaja al
  pedirlo (progreso de ambos lados, contenido idéntico, aviso de descargado), nombres repetidos, cerrar
  una oferta, archivo que cambió después de ofrecerse, corte a mitad de descarga sin archivos a medias,
  versión anterior sin archivos (error claro, el texto sigue andando), límites y persona desconectada,
  nombres peligrosos (`..\`, `CON`, flujos `:`, caracteres bidi), un bloque entra en una trama.
- **Está escribiendo**: llega a la persona, se apaga y se borra al desconectarse.
- **Actualización automática**: descarga verificada que reemplaza el ejecutable y guarda el anterior,
  versión igual o vieja ignorada, hash o tamaño distinto sin tocar nada, solo HTTPS desde GitHub,
  manifiestos inválidos o enormes, sin internet, vuelta atrás a la versión anterior sin reintentar la
  nueva, restos de una actualización anterior, búsqueda programada y el manifiesto tal como lo genera el CI.

## Decisiones técnicas

| Tema | Decisión | Motivo |
|---|---|---|
| Transporte | TCP con tramas por longitud | Más liviano y simple que WebSocket; ver [PROTOCOL.md](docs/PROTOCOL.md) |
| Topología | P2P en malla: una conexión por cada par, cualquiera marca | Sin servidor; robusto si el firewall bloquea en un solo sentido; arbitraje determinista |
| Seguridad | Identidad ECDH P-256 con id = hash de la clave; ECDH estático → clave de enlace; HMAC mutuo + AES-GCM por sesión | Sin códigos ni contraseñas, sin confiar en la IP ni en el nombre, mensajes privados en la LAN |
| Contactos | Automáticos al verificar la identidad; bloqueo local | Modo oficina sin fricción; quien molesta se bloquea |
| Clave en disco | DPAPI (usuario actual) | No sirve si se copia el archivo |
| Overlay | `HwndSource` en vez de `Window` | Control exacto de estilos Win32 y DPI del monitor destino |
| Bandeja | `Shell_NotifyIcon` propio | Sin cargar WinForms; reinstala el icono si el Explorador se reinicia |
| Inicio con Windows | `HKCU\...\Run` | Por usuario, sin administrador, sin servicios ni tareas programadas |
| Instalador | Inno Setup (solo al compilar) | Instalador estándar y pequeño; alternativa portátil sin dependencias |
| Publicación | single-file autocontenido, sin comprimir, sin trimming | Comprimir sube la RAM al arrancar; WPF no admite trimming |
| Mensajes en espera | 2 min, máx. 20 por persona, en memoria | Un "susurro" viejo no tiene sentido; nada se escribe a disco |
| Imágenes y archivos | Por la misma sesión cifrada, en bloques de 32 KB; imágenes en memoria (≤ 10 MB), archivos solo si se piden | Sin servidor ni carpetas compartidas; nada viaja ni se guarda sin que la persona lo decida |
| Compatibilidad | Capacidad `files` anunciada en el saludo (sin subir la versión del protocolo) | La 2.1 sigue hablando con la 2.0.0: texto sí, archivos solo con quien los entiende |
| Actualizaciones | Manifiesto + `.exe` de la release de GitHub, SHA-256, renombrar el `.exe` en uso y reiniciar sin que se note | Sin servicio de actualización ni permisos de administrador; misma ruta (firewall e inicio con Windows intactos); vuelta atrás si la nueva no arranca |
| CI | GitHub Actions en Windows; la release sale de un tag `vX.Y.Z` | Tests, `.exe`, instalador y manifiesto siempre salen del mismo lugar |
