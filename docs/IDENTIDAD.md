# Identidad y seguridad (sin códigos)

Objetivo: que toda la oficina pueda usar Susurro **sin vincular ni escribir códigos**, sin
contraseñas ni cuentas, y aun así sin confiar en direcciones IP ni permitir que alguien se haga
pasar por otra PC.

## Cómo se usa

1. La primera vez, Susurro pregunta **tu nombre** (así te ven los demás; nunca se usa el nombre
   del equipo). Hasta entonces no sale a la red.
2. Todas las PCs con Susurro abierto en la misma red se encuentran solas y aparecen en la lista
   **Para:** de la ventana principal.
3. Elegís a una persona, o **Todos los conectados**, y escribís.

Si alguien no aparece solo (redes separadas, multicast bloqueado): *Configuración → Personas →
Agregar por dirección* con su IP o nombre de equipo.

## Qué ocurre por dentro

```
Primera ejecución:  par de claves ECDH P-256 (privada guardada con DPAPI)
                    InstanceId = SHA-256(clave pública)[0..16]  (32 caracteres hex)

Al conectar A y B:  cada uno envía su clave pública y su id
                    cada uno verifica id == SHA-256(clave pública)
                    K = HKDF-SHA256(ECDH(privada propia, pública ajena), sal = ids ordenados)
                    autenticación mutua HMAC con K + sesión AES-256-GCM (ver PROTOCOL.md)
```

* **El id es el hash de la clave pública**: para presentarse con el id de otra PC hay que tener su
  clave privada. Copiar el id, la IP o el nombre no alcanza.
* **La clave de enlace nunca viaja**: sale de ECDH entre las identidades. Un tercero en la red no
  puede leer los mensajes ni inyectar tramas.
* **Contacto nuevo = identidad verificada**: una PC recién descubierta se agrega a la lista recién
  después de completar el saludo criptográfico. Los datagramas de descubrimiento no alcanzan.

## Qué se guarda

En `%LOCALAPPDATA%\Susurro\settings.json`:

| Dato | Para qué |
|---|---|
| `identityKey` | Clave privada de identidad **cifrada con DPAPI** (usuario actual de Windows) |
| `instanceId` | Se deriva de la clave (se recalcula al arrancar) |
| `friendlyName` | Tu nombre, tal como lo ven los demás |
| `contacts[]` | id, nombre, última IP/puerto (solo pistas), dirección manual, bloqueado |
| `lastRecipient` | A quién le escribiste la última vez (se preselecciona) |

* No se guardan contraseñas ni mensajes.
* Copiar `settings.json` a otra PC u otro usuario **no sirve** para suplantar: DPAPI no podrá
  descifrar la clave; Susurro crea una identidad nueva y aparece como otra persona.

## Bloquear y quitar

* **Bloquear** (*Configuración → Personas*): esa identidad no puede conectarse ni enviarte mensajes
  hasta que la desbloquees. Queda en la lista (al final) para poder desbloquearla.
* **Quitar de la lista**: para PCs que ya no se usan. Solo con la persona desconectada; si vuelve a
  conectarse, aparece de nuevo.

## Modelo de amenazas (qué cubre y qué no)

Cubre: leer mensajes en la red, repetir/inyectar/reordenar tramas, suplantar a una PC que ya está
en tu lista (misma IP, mismo nombre o mismo `InstanceId`), y abusar del descubrimiento UDP para
llenar la lista (los contactos se agregan solo tras verificar la identidad, con límites).

**No cubre** (es el precio de no usar códigos):

* **Cualquiera con Susurro en la misma red puede escribirte.** Es la idea del modo oficina; si
  alguien molesta, bloquealo.
* **Los nombres no se verifican.** Alguien podría llamarse igual que un compañero. Esa persona
  aparece como un contacto **distinto** (otro id): los mensajes que le mandás al contacto que ya
  tenías siguen yendo solo a la PC original. Ante la duda, en *Personas* se ve la IP de cada uno.
* Alguien con acceso a la sesión de Windows del usuario (puede usar Susurro directamente).

## Actualizaciones automáticas

La actualización no pasa por la red de la oficina: cada PC baja la versión nueva de la release de
GitHub del repositorio, por HTTPS.

* El manifiesto (`susurro-update.json`) se pide a `github.com` y solo se aceptan descargas por HTTPS
  desde `github.com` o `*.githubusercontent.com`. Antes de tocar el ejecutable se verifican el
  **tamaño y el SHA-256** del manifiesto; si no coinciden, se borra la descarga y no cambia nada.
* La confianza es la misma que al bajar `Susurro.exe` a mano desde Releases: quien controle la
  cuenta de GitHub del repositorio (o su CI) puede publicar una versión. Protegé esa cuenta con 2FA.
* Para reemplazarse sin permisos de administrador, el instalador deja la carpeta del programa con
  permiso de escritura para los usuarios de la PC. Un programa que ya corre como ese usuario podría
  cambiar `Susurro.exe`; no le da más permisos de los que ya tiene, pero conviene saberlo en PCs
  compartidas. Si no lo querés, desactivá *Actualizar automáticamente* y quitá ese permiso.
