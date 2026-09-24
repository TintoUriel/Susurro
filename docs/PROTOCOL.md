# Protocolo de Susurro (v1)

Susurro usa **TCP** directo entre las dos PCs (puerto por defecto **47810**) y **UDP** para el
descubrimiento (puerto **47811**, multicast `239.255.77.77` + broadcast dirigido a la subred).

## ¿Por qué TCP y no WebSocket?

Las dos PCs son las únicas participantes, no hay navegador ni proxies HTTP en el medio.
WebSocket agregaría un handshake HTTP, dependencia de `HttpListener`/reservas de URL (permisos de
administrador en Windows) y encabezados por trama, sin ningún beneficio aquí. TCP con tramas
prefijadas por longitud es más simple, más liviano y totalmente orientado a eventos.

## Tramas

```
[ longitud: uint32 big-endian ][ payload ]
```

* Antes de autenticar: payload = JSON UTF-8, máximo **4 KB** (limita abuso desde la red).
* Después de autenticar: payload = `AES-256-GCM(JSON)` + etiqueta de 16 bytes, máximo **16 KB**.
* El tamaño se valida **antes** de reservar memoria.
* El JSON es un único tipo "plano" (`Packet`) con campos opcionales; los campos desconocidos se
  ignoran (compatibilidad hacia adelante) y los nulos no se envían.

## Sesión

Cualquiera de las dos PCs puede iniciar la conexión ("quien marca" = D, "quien escucha" = L).

```
D → L  hello   { v:1, mode:"session", id, name, boot, nonce(16B), port, ts }
L → D  welcome { v:1, id, name, boot, nonce(16B), proof = HMAC(K, "auth/L" ‖ idD ‖ idL ‖ nonceD ‖ nonceL), port, ts }
D → L  auth    { proof = HMAC(K, "auth/D" ‖ idD ‖ idL ‖ nonceD ‖ nonceL) }
          --- a partir de aquí todo va cifrado ---
L → D  ok      { name }      (o reject{reason:"duplicate"})
```

* `K` es la **clave de vínculo** (32 bytes) acordada al vincular. Nunca viaja por la red.
* Ambos lados se autentican mutuamente; un tercero que no tenga `K` no puede completar el saludo
  (ni como quien marca ni como quien escucha). No se confía en la IP.
* Claves de sesión: `HKDF-SHA256(K, salt = nonceD ‖ nonceL, info = "…/d2l" | "…/l2d")`: una clave
  distinta por dirección y por conexión.
* Nonce de GCM = contador implícito de 64 bits por dirección. Una trama repetida, omitida,
  reordenada o inyectada falla la verificación y la sesión se cierra (luego se reconecta sola).
* Si `id` no es la PC vinculada → `reject{reason:"unknown"}` (la otra PC lo muestra como
  "La otra PC no reconoce este vínculo"). Registros de rechazos: como máximo uno cada 5 minutos.
* Máximo 4 saludos simultáneos en curso y 8 s de tiempo límite por saludo.

### Conexiones duplicadas (ambos marcan a la vez)

Regla determinista, sin negociación (`SessionArbiter`): la conexión **preferida** es la iniciada por
la instancia con el `InstanceId` menor.

| existente    | llega        | resultado                                   |
|--------------|--------------|---------------------------------------------|
| cualquiera   | preferida    | la nueva reemplaza a la existente           |
| no preferida | no preferida | la nueva reemplaza (la vieja estaba muerta) |
| preferida    | no preferida | se rechaza la nueva y se hace *ping* a la existente |

Ambos extremos llegan a la misma conclusión sin importar el orden de llegada (hay tests).

## Mensajes

```
msg  { msgId(32 hex), seq, text(≤300), urgent?, receipt?, ts, name }
ack  { msgId, state:"received" }       ← siempre (también ante duplicados)
ack  { msgId, state:"shown" }          ← solo si el remitente pidió receipt (Confirmar recepción)
```

* **Enviado / Entregado / Visto**: `Sent` al escribir en el socket, `Delivered` al recibir
  `ack received`, `Shown` al recibir `ack shown` (el overlay lo mostró).
* **Bandeja de salida**: si no hay conexión, el mensaje espera hasta **2 minutos** (máx. 20 en espera).
  Al reconectar se envían en orden. Si una sesión cae con mensajes sin confirmar, se reenvían en
  la siguiente.
* **Duplicados**: el receptor recuerda los últimos 512 `msgId`; un reenvío se confirma otra vez
  pero no se muestra dos veces.
* **Fuera de orden**: la cola del overlay ordena por hora de envío (corregida por el desfase de
  reloj medido en el saludo) y luego por `seq`.
* **Mensajes viejos**: se descartan si tienen más de 10 minutos.
* **Validación**: el texto se limpia igual al enviar y al recibir (sin caracteres de control ni
  marcas bidi, espacios colapsados) y se rechaza si está vacío o supera 300 caracteres.

## Latido (solo cuando hace falta)

* Nada de sondeo: la lectura es asíncrona (0 % CPU en reposo).
* Un temporizador cada 15 s mira *cuánto hace que no llega nada*. Solo si pasaron **30 s** sin tráfico,
  quien marcó envía `ping` (el otro responde `pong`): 2 paquetes pequeños cada 30 s con la sesión
  inactiva.
* Sin recibir nada durante **75 s** → la sesión se da por muerta y se reconecta.
* Cierre ordenado: `bye` al salir o apagar Windows, así la otra PC lo sabe al instante.

## Reconexión

Bucle único por instancia, dormido mientras hay sesión:

1. Probar direcciones candidatas: la vista por última vez en el descubrimiento, la última dirección
   conocida (se guarda en la configuración) y la dirección manual (si se configuró).
2. Si fallan: consulta de descubrimiento (≈3 datagramas, máx. 2,5 s) buscando el `InstanceId`.
3. Espera creciente con ±20 % de azar: 1, 2, 5, 10, 20, 30, 60, 60… s.
4. Se despierta antes si: la otra PC se anuncia por UDP, cambia la red (IP, cable, Wi-Fi), el
   equipo vuelve de suspensión o el usuario pulsa "Reconectar ahora".

Con la otra PC apagada, el costo es ~1 intento TCP + 3 datagramas UDP por minuto.

## Descubrimiento (UDP)

```json
{ "s":"susurro", "v":1, "t":"q|r|a", "id":"…", "name":"…", "port":47810, "pairing":false, "paired":true }
```

* `q` consulta (multicast + broadcast de cada subred), `r` respuesta unicast, `a` anuncio.
* Anuncios solo al iniciar, al cambiar la red, al reanudar y al generar un código de vinculación.
* Respuestas limitadas a 20 por segundo. Datagramas de más de 1 KB se ignoran.
* La información UDP **no es confiable**: solo aporta direcciones candidatas; la identidad se
  verifica siempre con la criptografía de la sesión.

## Detección de problemas

| Situación | Qué ve el usuario |
|---|---|
| Otra PC apagada | × Desconectado (reintenta solo) |
| Responde por UDP pero el TCP no abre | "La otra PC responde, pero el puerto TCP no es accesible (¿firewall?)" |
| La otra PC no tiene este vínculo | "La otra PC no reconoce este vínculo. Volvé a vincular." |
| Puerto local ocupado | "El puerto N está en uso por otro programa" (reintenta cada vez más espaciado) |
