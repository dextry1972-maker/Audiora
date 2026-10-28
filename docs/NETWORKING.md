# Audiora — Red y control del Sonos

Cómo encuentra Audiora el altavoz, cómo le sirve el audio y cómo vigila la conexión. Valores del código confirmado en `a7e2b36` (28/10/2026). Cadena de audio: [AUDIO_PIPELINE.md](AUDIO_PIPELINE.md).

## 1. Topología actual

| Elemento | Valor |
|---|---|
| Red | **Zona con cobertura inalámbrica móvil de Windows** (Mobile Hotspot) creada por el PC; la tarjeta Wi-Fi no está conectada a ninguna otra red |
| PC | `192.168.137.1` (comprobado en `app-events.log`: `transmitiendo …: http://192.168.137.1:8780/sonosflow.wav`) |
| Altavoz | Sonos Five «Bedroom» (S24, firmware Sonos/96.1), `192.168.137.158`, cliente de la zona |
| Audio | el Sonos **descarga** `http://192.168.137.1:8780/sonosflow.wav` (User-Agent `Linux UPnP/1.0 Sonos/96.1-79270 (ZPS24)`) |
| Control | SOAP UPnP a `http://192.168.137.158:1400` |

Consecuencias: si el PC se apaga, el Sonos pierde la red. Hipótesis **no confirmada** sobre los cortes de ~6 min: la radio del PC busca redes periódicamente y provoca microcortes en la zona ([INVESTIGACION-CORTES-6MIN.md](INVESTIGACION-CORTES-6MIN.md)).

## 2. Descubrimiento SSDP — `Core/Sonos/SonosDiscovery.cs`

1. Interfaces IPv4 activas, no loopback y con multidifusión (incluye VPN y adaptadores virtuales).
2. Por cada una, un `UdpClient` ligado a esa IP (si la dirección ya no existe, p. ej. mientras se levanta la zona Wi-Fi, esa interfaz se omite y siguen las demás) envía **dos** `M-SEARCH` a `239.255.255.250:1900` separados 100 ms: `MAN: "ssdp:discover"`, `MX: 1`, `ST: urn:schemas-upnp-org:device:ZonePlayer:1`.
3. Escucha respuestas durante el `timeout` (la App usa **3 s**). Acepta las que tienen `LOCATION` y mencionan el `ST`.
4. Descarga cada `LOCATION` (XML de descripción; `HttpClient` de 3 s) y lee `roomName`, `modelName`, `modelNumber`, `UDN`. La IP del altavoz sale del host de `LOCATION`, no del remitente UDP.
5. Quita duplicados por `UDN` y ordena por sala.

La App elige: el último altavoz usado (`LastSpeakerUdn`), si no el primero cuyo modelo contiene «Five», si no el primero. Volver a buscar solo es posible sin transmisión activa.

## 3. IP local e interfaz

`SonosDiscovery.LocalAddressFor(sonos)` conecta un socket UDP a `sonos:1400` (no envía nada) y lee su dirección local: es la interfaz que la tabla de rutas usaría para llegar al Sonos, la única a la que el Sonos puede volver a llamar. El servidor escucha **solo en esa IP** (no en `0.0.0.0`) y en el puerto de los ajustes (8780 por defecto). Solo IPv4.

## 4. Servidor HTTP — `Core/Net/WavStreamServer.cs`

- `TcpListener` propio, no `HttpListener`: no necesita reserva de URL (URL ACL) ni administrador.
- **Lista blanca**: IP de todos los altavoces descubiertos + el destino (constructor de `StreamingSession`). Cualquier otra conexión se cierra al aceptarla y suma `RejectedConnections` (no se anota en los registros). Ante una `SocketException` al aceptar, el bucle espera 100 ms y sigue.
- Petición: lee hasta 8192 B o hasta `\r\n\r\n`, con **5 s** de plazo; si no llega, cierra.
- Ruta que empiece por `/sonosflow.wav` y método `GET` o `HEAD`; lo demás → `404`. `HEAD` recibe solo cabeceras.
- Respuesta `200 OK` sin `Content-Length`, `Connection: close` (el cuerpo dura hasta que alguien cierra):

```
Content-Type: audio/wav
Cache-Control: no-cache, no-store
Accept-Ranges: none
transferMode.dlna.org: Streaming
contentFeatures.dlna.org: DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000
Server: SonosFlow/1.0
```

- Después: cabecera WAV de 44 B, el margen anti-cortes y el directo. Sin rangos (`Range` solo se registra).
- Cada conexión y desconexión se anota en el CSV y en `app-events.log` («red: …»): `conexión <ip:puerto> GET /sonosflow.wav [Range=…] UA=…` y `desconexión <ip:puerto> tras N s: <motivo>` (`detenido` al parar Audiora; `cerrada por el vigilante (no recogía audio)` cuando un reintento cierra la conexión con `AbortClients`).

## 5. Control UPnP — `Core/Sonos/SonosClient.cs`

`HttpClient` con **5 s** de plazo, `POST` con `SOAPACTION: "<servicio>#<acción>"`; las consultas de vigilancia de la sesión se acortan a **2 s** (`StreamingSession.ProbeTimeout`). Una respuesta no 2xx lanza `SonosSoapException` con el `errorCode` UPnP; el 800 se traduce como «altavoz agrupado que no es el principal».

| Servicio / acción | Uso |
|---|---|
| AVTransport `SetAVTransportURI` | URI del stream + DIDL-Lite (título «Audiora — audio del PC», `object.item.audioItem.musicTrack`, `protocolInfo http-get:*:audio/wav:*`) |
| AVTransport `Play` (`Speed 1`) / `Stop` | inicio y fin de la transmisión (`Stop` no se envía si la sesión fue liberada) |
| AVTransport `Pause` (`PauseAsync`) | solo el autotest (parte C), para simular una pausa desde la app de Sonos |
| AVTransport `GetTransportInfo` | estado cada segundo (sesión, 2 s) y cada 3 s sin sesión (¿en línea?) |
| AVTransport `GetPositionInfo` | `RelTime`, solo en `PLAYING`/`TRANSITIONING` (2 s): estimación del búfer y detección de posición congelada. Si falla, la posición queda desconocida; **no** convierte `PLAYING` en `UNREACHABLE` |
| AVTransport `GetMediaInfo` (`GetCurrentUriAsync`) | antes de cada reintento (2 s): ¿la URI activa sigue siendo nuestro stream? |
| RenderingControl `Get/SetVolume` (Master, 0–100) | control de volumen de la App y atajos ±5 |
| RenderingControl `Get/SetBass`, `Get/SetTreble` (−10…+10), `Get/SetLoudness` | ecualizador del propio Sonos |

Audiora solo escribe volumen o ecualizador cuando el usuario los cambia (antirrebote 120/150 ms); el sondeo de 3 s no reescribe lo leído.

## 6. Ciclo de una conexión

```mermaid
sequenceDiagram
  participant A as Audiora (StreamingSession)
  participant S as Sonos (puerto 1400)
  A->>A: LocalAddressFor, crea la fuente, servidor escuchando, captura en marcha, silencio local
  A->>A: _sentAtPlaybackStart = BytesSent
  A->>S: SOAP SetAVTransportURI(http://PC:8780/sonosflow.wav)
  S->>A: GET /sonosflow.wav (a los 0,2–0,5 s, aún dentro de la llamada)
  A-->>S: 200 + cabecera WAV + margen (con silencio delante si falta) + directo
  A->>S: SOAP Play
  loop cada ~1 s
    A->>S: GetTransportInfo (+ GetPositionInfo si PLAYING), 2 s cada una
  end
  opt no sana durante 5 s (RecoveryPolicy)
    A->>S: GetMediaInfo: ¿sigue siendo nuestra URI? (si no, o pausa, o Stop repetido: Released)
    A->>A: AbortClients
    A->>S: SetAVTransportURI + Play
  end
  A->>S: SOAP Stop (al detener; no si la sesión fue liberada)
  A-->>S: cierre del socket
```

Si `StartAsync` falla, se deshace todo sin enviar `Stop` y la App muestra la causa (puerto ocupado, Sonos sin respuesta, grupo, captura imposible). Al iniciar Windows, la App reintenta durante un máximo de 5 min ([ARCHITECTURE.md](ARCHITECTURE.md) §7).

## 7. Vigilancia y reconexión — `StreamingSession.MonitorAsync`

Cada vuelta (1 s + respuestas):

| Paso | Regla |
|---|---|
| Consulta (`ProbeAsync`) | `GetTransportInfo` con 2 s; error HTTP, plazo, SOAP o XML → `UNREACHABLE` y `core.speakerUnreachable` (el aviso se borra en cuanto vuelve a responder). En `PLAYING`/`TRANSITIONING`, además `GetPositionInfo` con 2 s (si falla: posición desconocida) |
| Captura caída | `CaptureError` → publica la fila y pasa a `Failed`; la App detiene la sesión y muestra el error |
| App seguida | en «Una aplicación», cada 3 vueltas comprueba el proceso y lo sigue si reaparece |
| Veredicto | `PlaybackWatchdog.Observe` (tabla siguiente). Cada cambio se anota: `vigilante: <veredicto> (Sonos <estado>, clientes N)` / `vigilante: reproducción correcta`. Sano y en `Recovering` → vuelve a `Streaming` |
| Decisión | `RecoveryPolicy.Observe` → nada, reintento o abandono |

**`PlaybackWatchdog`** (`Core/Streaming/PlaybackWatchdog.cs`). El silencio nunca es un fallo: siempre se envían tramas, así que un Sonos sano sigue recogiendo bytes y su posición avanza también en silencio.

| Veredicto | Condición |
|---|---|
| `UNREACHABLE` (fallo del canal de control) | **con el audio fluyendo** (≥ 1 cliente y `SinceLastClientProgress` < umbral de `NotConsuming`) cuenta como sano durante **30 s** (`UnreachableTolerance`): un fallo de SOAP no reemite ni corta la conexión de datos, porque reemitir causaría el corte que se quiere evitar. Sin audio fluyendo, o pasados 30 s → `NotPlaying` |
| `NotPlaying` | estado distinto de `PLAYING`/`TRANSITIONING`, 0 conexiones HTTP, o `TRANSITIONING` más de **15 s**. Se evalúa siempre, también durante el arranque |
| (arranque) | los primeros **10 s** tras conectar o tras cada reintento (`Reset`) cuentan como sanos salvo `NotPlaying` |
| `NotConsuming` | ningún cliente recoge audio (`SinceLastClientProgress`) durante **máx(5 s, margen + 3 s)**: 5 s con margen 0–2 s, 6 s con 3 s |
| `Frozen` | en `PLAYING`, la posición se vio avanzar al menos 2 veces y lleva **8 s** sin cambiar. Una lectura de posición fallida (desconocida) **reinicia** el contador de congelado: las lecturas fallidas nunca suman hacia `Frozen` |
| `Healthy` | lo demás |

**`RecoveryPolicy`** (mismo archivo):

| Regla | Valor |
|---|---|
| Espera antes del primer intento | 5 s seguidos no sanos (`Debounce`): los fallos más cortos no reconectan |
| Esperas entre intentos | 5, 10, 20, 40 y luego 60 s (intentos a los 5, 10, 20, 40, 80, 140, 200 y 260 s de fallo continuo, según la prueba) |
| Límite | 8 intentos (`MaxAttempts`): **el 8.º también recibe su espera completa** (60 s), así que en fallo continuo se abandona a los **320 s**; 10 min seguidos no sanos (`GiveUpAfter`) es solo el tope de seguridad. Abandono → `Failed`, «El Sonos no recupera la transmisión tras N intentos»; una vez abandonado no vuelve a intentar |
| Reinicio del contador | 30 s seguidos sanos (`HealthyReset`) |

**En cada reintento**, primero se comprueba si el usuario tomó el Sonos:

| Señal | Motivo de `fin: …` |
|---|---|
| `STOPPED` otra vez menos de **60 s** después de un reintento que se hizo por `STOPPED` | `core.stoppedFromSonos` (un `Stop` aislado se trata como fallo de red: se reemite una vez) |
| `GetMediaInfo` (2 s): la URI activa no es nuestro stream (`PlaybackWatchdog.IsOurStream`: compara host:puerto + ruta; una URI vacía cuenta como nuestra) | `core.otherSource` |
| estado `PAUSED_PLAYBACK` | `core.pausedFromSonos` |

En esos casos la sesión pasa a **`Released`**: no pelea con el usuario, no envía `Stop`, anota `fin: …` y la App se detiene sin error y sin reanudar al abrir. Si `GetMediaInfo` falla, se reintenta como siempre.

Si no, reintento: `reconnects++`, estado `Recovering`, nota `reintento N/8 (<veredicto>, Sonos <estado>)`, `AbortClients()` (una conexión atascada deja de contar como cliente), `SetAVTransportURI` + `Play` (si fallan por HTTP, plazo, SOAP o XML: `core.retryFailed|…`), `watchdog.Reset` y `StallTracker.RestartWarmup`.

El bucle no puede morir en silencio: cualquier excepción inesperada lo pasa a `Failed` («Error interno de la transmisión: …», anotado en `app-events.log`); un error que ocurra mientras se detiene no se publica como fallo. La limpieza al detener va por pasos y siempre libera PC, captura y puerto ([AUDIO_PIPELINE.md](AUDIO_PIPELINE.md) §7).

El sondeo de la App (`MainViewModel.PollSpeakerAsync`, 3 s, otro `SonosClient` con 5 s) lee además volumen y ecualizador: durante una sesión hay 1–2 consultas SOAP/s de la sesión + 4 cada 3 s de la App.

## 8. Plazos y periodos

| Constante | Valor | Dónde |
|---|---|---|
| Escucha SSDP | 3 s (`MX: 1`, 2 envíos a 100 ms) | `MainViewModel.DiscoverAsync`, `SonosDiscovery.ListenAsync` |
| Descarga de la descripción | 3 s | `SonosDiscovery.DiscoverAsync` |
| Petición SOAP (App, arranque, `Stop`) | 5 s | `SonosClient` (`HttpClient.Timeout`) |
| Consultas de vigilancia (`GetTransportInfo`, `GetPositionInfo`, `GetMediaInfo`) | 2 s | `StreamingSession.ProbeTimeout` |
| Cabeceras HTTP del Sonos | 5 s | `WavStreamServer.ReadRequestAsync` |
| Pausa tras error al aceptar | 100 ms | `WavStreamServer.AcceptLoopAsync` |
| Vigilancia | 1 s + respuestas | `StreamingSession.MonitorLoopAsync` |
| Arranque del vigilante / `TRANSITIONING` / congelado | 10 s / 15 s / 8 s | `PlaybackWatchdog` |
| `UNREACHABLE` tolerado con audio fluyendo | 30 s | `PlaybackWatchdog.UnreachableTolerance` |
| Sin recoger audio | máx(5 s, margen + 3 s) | `PlaybackWatchdog.NotConsumingAfter` |
| Reintentos | 5 s de espera; 5/10/20/40/60 s; 8 intentos con su espera (abandono a los 320 s en fallo continuo); tope 10 min; reinicio tras 30 s sano | `RecoveryPolicy` |
| `STOPPED` repetido tras reemitir | < 60 s → `Released`; se olvida tras 30 s reproduciendo bien (una Wi-Fi inestable no termina la sesión) | `StreamingSession.MonitorLoopAsync` |
| Arranque ignorado por el detector de atascos | 3 s (también tras cada reintento) | `StallTracker` |
| Seguimiento de la app | cada 3 vueltas | `StreamingSession.MonitorLoopAsync` |
| Sondeo de la App | 3 s | `MainViewModel` (`_poll`) |
| Lista de apps con audio | 2,5 s | `MainViewModel.Sources` (`_appsTimer`) |
| Espera de arranque | 2, 3, 5, 8, 13, 20 y 30 s; máx. 5 min | `MainViewModel.Startup` |
| Activación de captura por aplicación | 5 s | `ProcessLoopbackCapture.Activate` |
| Cabecera WAV | ≈ 6,2 h a 192 kB/s | `WavStreamServer.BuildWavHeader` |

Ninguno actúa durante una reproducción sana; los de `RecoveryPolicy` solo corren mientras el Sonos no está sano (revisión de temporizadores: [INVESTIGACION-CORTES-6MIN.md](INVESTIGACION-CORTES-6MIN.md)).

## 9. Firewall

- Audiora **no crea ni modifica reglas**. La primera vez que el Sonos se conecta, Windows pregunta: permitir **solo en redes privadas**.
- El permiso queda asociado al ejecutable; `SonosFlow.CaptureCheck.exe` no lo tiene, así que no puede recibir conexiones del Sonos: su modo `session <ip>` no sirve en este PC y las pruebas con el altavoz van por `SonosFlow.exe --selftest-session` ([TESTING.md](TESTING.md)).
- Las pruebas no deben cambiar la red ni el firewall (CLAUDE.md).

## 10. Lo que NO se hace

- No se expone nada a Internet: sin UPnP-IGD ni apertura de puertos en el router, sin escucha en todas las interfaces, sin API en la nube de Sonos, sin telemetría.
- Sin TLS ni autenticación: el único control de acceso es la lista blanca por IP dentro de la red local.
- Sin IPv6, mDNS ni AirPlay.
- Sin control de grupos: con un altavoz agrupado hay que elegir el principal.
- No cambia el volumen ni el ecualizador del Sonos por su cuenta.
- No le disputa el altavoz al usuario: si el Sonos pasa a otra fuente, se pausa o se detiene dos veces seguidas desde la app de Sonos, Audiora se retira (`Released`).
