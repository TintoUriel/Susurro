# Protocolo de Susurro (v2)

Susurro usa **TCP** directo entre cada par de PCs (puerto por defecto **47810**) y **UDP** para el
descubrimiento (puerto **47811**, multicast `239.255.77.77` + broadcast dirigido a la subred).
No hay servidor: cada PC mantiene una conexión con cada compañero conectado.

La v2 reemplaza la vinculación con código de la v1 por identidades de clave pública
(ver [IDENTIDAD.md](IDENTIDAD.md)). Una v1 y una v2 no se conectan entre sí: la v2 responde
`reject{reason:"version"}` y el descubrimiento ignora los datagramas de otra versión.

## ¿Por qué TCP y no WebSocket?

Solo participan PCs de la LAN, sin navegador ni proxies HTTP en el medio. WebSocket agregaría un
handshake HTTP, dependencia de `HttpListener`/reservas de URL (permisos de administrador en
Windows) y encabezados por trama, sin ningún beneficio aquí. TCP con tramas prefijadas por
longitud es más simple, más liviano y totalmente orientado a eventos.

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
D → L  hello   { v:2, mode:"session", id, name, key, boot, nonce(16B), port, ts }
L → D  welcome { v:2, id, name, key, boot, nonce(16B), proof = HMAC(K, "auth/L" ‖ idD ‖ idL ‖ nonceD ‖ nonceL), port, ts }
D → L  auth    { proof = HMAC(K, "auth/D" ‖ idD ‖ idL ‖ nonceD ‖ nonceL) }
          --- a partir de aquí todo va cifrado ---
L → D  ok      { name }      (o reject{reason:"duplicate"})
```

* `key` es la clave pública de identidad (ECDH P-256) y `id = SHA-256(key)[0..16]` en hexadecimal.
  Cada lado comprueba que el `id` recibido corresponda a su `key` **antes** de seguir: nadie puede
  presentarse con el id de otra PC.
* `K` es la **clave de enlace** (32 bytes) = `HKDF-SHA256(ECDH(privada propia, key de la otra), sal = ids ordenados)`.
  Ambos lados la calculan por su cuenta; nunca viaja por la red. Solo quien tenga la clave privada
  de ese id puede producir la prueba HMAC: la autenticación es mutua y no se confía en la IP.
* Claves de sesión: `HKDF-SHA256(K, salt = nonceD ‖ nonceL, info = "…/d2l" | "…/l2d")`: una clave
  distinta por dirección y por conexión.
* Nonce de GCM = contador implícito de 64 bits por dirección. Una trama repetida, omitida,
  reordenada o inyectada falla la verificación y la sesión se cierra (luego se reconecta sola).
* Quien completa el saludo por primera vez se agrega solo a los contactos. Si está **bloqueado**:
  `reject{reason:"unknown"}` (la otra PC lo muestra como "No acepta conexiones de esta PC").
* Otros rechazos: `version` (protocolo distinto), `auth` (clave o prueba inválida), `self` (se
  conectó consigo misma). Registros de rechazos: como máximo uno cada 5 minutos.
* Máximo 32 saludos entrantes simultáneos y 8 s de tiempo límite por saludo.

### Conexiones duplicadas (ambos marcan a la vez)

Se decide por cada par de PCs, sin negociación (`SessionArbiter`): la conexión **preferida** es la
iniciada por la instancia con el `InstanceId` menor.

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
profile { name }                       ← cambio de nombre de la persona
```

* Cada mensaje va a **una** persona. "Todos los conectados" envía una copia (con su propio
  `msgId`) a cada persona conectada; la ventana resume "Entregado a 3 de 4".
* **Enviado / Entregado / Visto**: `Sent` al escribir en el socket, `Delivered` al recibir
  `ack received`, `Shown` al recibir `ack shown` (el overlay lo mostró; en un mensaje importante
  —`urgent: true`— recién cuando la persona le hizo clic para cerrarlo).
* **Bandeja de salida**: si no hay conexión con esa persona, el mensaje espera hasta **2 minutos**
  (máx. 20 en espera por persona). Al reconectar se envían en orden. Si una sesión cae con mensajes
  sin confirmar, se reenvían en la siguiente.
* **Duplicados**: el receptor recuerda los últimos 1024 `msgId`; un reenvío se confirma otra vez
  pero no se muestra dos veces.
* **Fuera de orden**: la cola del overlay ordena por hora de envío (corregida por el desfase de
  reloj medido en el saludo) y luego por `seq`.
* **Mensajes viejos**: se descartan si tienen más de 10 minutos.
* **Validación**: el texto se limpia igual al enviar y al recibir (sin caracteres de control ni
  marcas bidi, espacios colapsados) y se rechaza si está vacío o supera 300 caracteres.

## Latido (solo cuando hace falta)

* Nada de sondeo: la lectura es asíncrona (0 % CPU en reposo).
* Un temporizador cada 15 s mira *cuánto hace que no llega nada*. Solo si pasaron **30 s** sin tráfico,
  quien marcó envía `ping` (el otro responde `pong`): 2 paquetes pequeños cada 30 s por sesión
  inactiva.
* Sin recibir nada durante **75 s** → la sesión se da por muerta y se reconecta.
* Cierre ordenado: `bye` al salir o apagar Windows, así las demás PCs lo saben al instante.

## Reconexión

Un bucle por contacto, **dormido** hasta que haya un motivo para conectar:

* **Motivos**: arranque de Susurro (un intento con la última dirección conocida), anuncio UDP de esa
  PC, cambio de red o vuelta de suspensión, mensaje en espera para esa persona, conexión perdida o
  el botón "Buscar de nuevo".
* **Candidatas**: la dirección vista en el descubrimiento, la última conocida (se guarda en la
  configuración) y la dirección agregada a mano (si la hay).
* Si fallan y **hay mensajes esperando**: consulta de descubrimiento (≈3 datagramas, máx. 2,5 s)
  buscando ese `InstanceId`.
* **Reintentos**: espera creciente con ±20 % de azar (1, 2, 5, 10, 20, 30, 60, 60… s) solo mientras
  haya mensajes esperando o durante 15 minutos tras perder una conexión que no avisó el cierre.
  Si la otra PC se cerró con `bye` o nunca estuvo conectada, no se insiste: vuelve a aparecer
  cuando se anuncia al arrancar.

Con compañeros apagados, en régimen **no hay tráfico periódico**.

## Descubrimiento (UDP)

```json
{ "s":"susurro", "v":2, "t":"q|r|a", "id":"…", "name":"…", "port":47810 }
```

* `q` consulta (multicast + broadcast de cada subred), `r` respuesta unicast, `a` anuncio.
* Anuncios solo al iniciar, al cambiar la red, al reanudar y al cambiar el nombre. Una consulta al
  iniciar, al cambiar la red y con "Buscar de nuevo".
* Al ver un `id` desconocido se intenta una conexión TCP (que verifica su identidad): máximo 8 a la
  vez, 64 en cola, y un id que falló no se reintenta durante 60 s.
* Respuestas limitadas a 20 por segundo. Datagramas de más de 1 KB o de otra versión se ignoran.
* La información UDP **no es confiable**: solo aporta direcciones candidatas; la identidad se
  verifica siempre con la criptografía de la sesión.

## Detección de problemas

| Situación | Qué ve el usuario |
|---|---|
| Compañero apagado o con Susurro cerrado | ○ desconectado (se reconecta solo cuando vuelve) |
| Responde por UDP pero el TCP no abre | "Responde, pero el puerto TCP no es accesible (¿firewall?)" |
| La otra PC te bloqueó | "No acepta conexiones de esta PC." |
| Puerto local ocupado | "El puerto N está en uso por otro programa" (reintenta cada vez más espaciado) |
