# Vinculación (pairing)

Objetivo: que **solo** las dos PCs de la oficina puedan enviarse mensajes, sin contraseñas, sin
cuentas y sin confiar en direcciones IP.

## Cómo se usa

1. En la PC **A**: *Vincular… → Mostrar código → Generar código*. Aparece algo como `K7QM-4XPD`
   (válido 5 minutos, un solo uso).
2. En la PC **B**: *Vincular… → Ingresar código*. Elegí la PC A de la lista (se encuentra sola por la
   red) o escribí su IP/nombre, escribí el código y *Vincular*.
3. Las dos PCs muestran "✓ Vinculado con …" y se conectan solas desde ese momento, para siempre
   (incluso si cambian de IP), hasta que se desvinculen o se vuelva a vincular.

## Qué ocurre por dentro

```
B → A  hello       { mode:"pair", id_B, name, pub_B (ECDH P-256), nonce_B }
A → B  welcome     { mode:"pair", id_A, name, pub_A, nonce_A }
           secreto = ECDH(priv, pub_otro)
           T       = SHA-256(ids ‖ claves públicas ‖ nonces)
           Kc      = PBKDF2-SHA256(código, sal = T, 20 000 iteraciones)
B → A  pairConfirm { HMAC(Kc, "J" ‖ T ‖ SHA-256(secreto)) }
A → B  pairConfirm { HMAC(Kc, "H" ‖ T ‖ SHA-256(secreto)) }   (o reject "code")
           K = HKDF-SHA256(secreto, sal = T, info = código)   ← clave de vínculo (32 bytes)
```

* **Intercambio ECDH efímero**: el secreto nunca viaja por la red.
* **Confirmación con el código**: cada lado demuestra que conoce el mismo código *y* el mismo
  secreto ECDH. Un intermediario (MITM) tiene secretos distintos con cada lado y no puede producir
  confirmaciones válidas sin conocer el código.
* **Código de 40 bits** (8 caracteres base32 de Crockford; se toleran minúsculas, guiones y
  confusiones O/0, I/L/1) + **PBKDF2 con 20 000 iteraciones**: adivinarlo fuera de línea es
  impráctico; en línea, el código se **invalida tras 5 intentos fallidos** y **expira a los 5 minutos**.
* Solo se acepta una vinculación a la vez; el código se consume al usarse.

## Qué se guarda

En `%LOCALAPPDATA%\Susurro\settings.json`:

| Dato | Para qué |
|---|---|
| `instanceId` propio (GUID aleatorio) | Identidad persistente de esta instalación |
| `peer.instanceId`, `peer.name` | A quién se aceptan conexiones |
| `peer.lastAddress`, `peer.lastPort` | Solo una pista para reconectar rápido (se revalida siempre) |
| `peer.protectedKey` | La clave de vínculo **cifrada con DPAPI** (usuario actual de Windows) |

* No se guardan contraseñas, ni el código, ni mensajes.
* Copiar `settings.json` a otra PC u otro usuario **no sirve** para suplantar: DPAPI no podrá
  descifrar la clave y Susurro pedirá volver a vincular.

## Volver a vincular / desvincular

* *Configuración → Conexión → Volver a vincular…* reemplaza el vínculo anterior (la PC vieja deja
  de ser aceptada).
* *Desvincular* borra el vínculo de esta PC; la otra verá "La otra PC no reconoce este vínculo".

## Modelo de amenazas (qué cubre y qué no)

Cubre: dispositivos de la LAN que intentan enviar mensajes, suplantar a una PC (misma IP o mismo
`InstanceId`), leer los mensajes en la red, repetir/inyectar tramas, o adivinar el código.

No cubre: alguien con acceso a la sesión de Windows del usuario de una de las dos PCs (puede usar
Susurro directamente), ni un atacante que vea el código en pantalla y actúe dentro de los 5 minutos.
