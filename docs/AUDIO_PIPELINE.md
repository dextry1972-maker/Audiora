# Audiora — Cadena de audio

Del sonido del PC al socket del Sonos. Valores tomados del código confirmado en `a7e2b36` (28/10/2026). Red y control: [NETWORKING.md](NETWORKING.md).

```
WasapiLoopbackCapture (salida completa)  ─┐
ProcessLoopbackCapture (una aplicación)  ─┼─► LoopbackSource ──► StreamingSession.OnPcm ──► WavStreamServer.Write ──► cola por cliente ──► socket
SilencePacer (relleno de silencio)       ─┘   (PCM 16 bit LE     (pausa = ceros; pico)      (historial del margen)     (200 trozos,
                                               estéreo)               └─► SpectrumAnalyzer                              DropWrite)
```

## 1. Captura

### 1.1 «Todo el PC» — `Core/Audio/LoopbackSource.cs`

- `WasapiLoopbackCapture(device)` de NAudio sobre la salida elegida (o la predeterminada, rol Multimedia). Captura **después del volumen de Windows y antes del silencio** (medido: INFORME-TECNICO §3).
- Formato de origen = formato de mezcla del dispositivo, sin remuestrear. En este PC: **48000 Hz, 32 bit float, 2 canales** (cabecera de `stream-*.csv`).
- NAudio entrega ráfagas de ~50 ms (INFORME-TECNICO §3).

### 1.2 «Una aplicación» — `Core/Audio/ProcessLoopbackCapture.cs`

| Parámetro | Valor |
|---|---|
| API | `ActivateAudioInterfaceAsync("VAD\Process_Loopback")`, `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`, modo `INCLUDE_TARGET_PROCESS_TREE` (`:141-143`). Windows 10 2004 (19041)+ |
| Espera de activación | 5 s; si no, `TimeoutException` (`:156`) |
| `IAudioClient.Initialize` | compartido; `LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY`; búfer 100 ms (`:70-73`) |
| Formato pedido | PCM 16 bit estéreo a la frecuencia del stream (48000 Hz al empezar en este modo, `StreamingSession.AppCaptureSampleRate`); **Windows convierte** |
| Bucle | evento con espera de 200 ms; lee todos los paquetes; `AUDCLNT_BUFFERFLAGS_SILENT` → ceros (`:110-123`) |
| Parada | `StopRecording` espera el hilo hasta 1000 ms |

La app capturada sigue sonando por su salida; la captura no depende del silencio ni del volumen maestro de esa salida (medido con `CaptureCheck endpoint`).

### 1.3 Conversión — `LoopbackSource.Describe` / `OnDataAvailable` (`:88-146`)

| Origen | Conversión |
|---|---|
| Float 32 bit (`IeeeFloat` o `WaveFormatExtensible` con subtipo float) | `round(x · 32767)` saturado a Int16 |
| PCM 16 bit (caso de la captura por aplicación) | se reescala ÷32768 y ×32767: no es copia bit a bit (diferencia < 0,001 dB) |
| Otro PCM (24/32 bit entero) | `NotSupportedException` al crear la fuente |
| Mono | se duplica a L y R |
| Más de 2 canales | se toman **solo los canales 0 y 1** (sin mezcla de centro/envolvente) |

Salida siempre `WaveFormat(sampleRate de origen, 16, 2)`: 4 bytes por trama, 192 000 B/s a 48 kHz. Sin dither.

## 2. Marcapasos de silencio (`LoopbackSource.cs:15-16, 140-185`)

WASAPI no entrega paquetes mientras no suena nada; sin relleno, el Sonos se quedaría sin datos.

- Hilo `SilencePacer` (AboveNormal) cada **10 ms** (`PacerIntervalMs`).
- Cuenta tramas emitidas frente al reloj (`Stopwatch`). Solo rellena un déficit real: `déficit = tramas_reloj − 120 ms − tramas_emitidas` (`AllowanceMs = 120`, 5760 tramas a 48 kHz). Hasta 1 s de ceros por pasada.
- Cuando llega audio, las tramas emitidas se acotan a ±120 ms del reloj: la deriva entre el reloj del dispositivo y el QPC nunca se convierte en silencio intercalado.
- `SilenceBytes` cuenta el relleno (columna `silence_kB`); `LastPeak = 0` en los tramos de relleno.
- `LoopbackSource.ActiveCount` (estático): capturas vivas en el proceso; el autotest exige 1 durante la sesión y 0 al detener.

## 3. Pausa (`StreamingSession.SetPaused` / `OnPcm`)

`SetPaused(true)` no toca la captura ni la conexión: `OnPcm` sustituye cada bloque por ceros del mismo tamaño (búfer inicial de 19 200 B = 100 ms, crece si hace falta). El Sonos sigue en `PLAYING` recibiendo silencio a ~192 kB/s; reanudar es inmediato. El pico se mide **después** de la pausa (−∞ en pausa) y el espectro recibe el silencio.

## 4. Cambio de fuente en caliente (`StreamingSession.SwitchSourceAsync`)

1. Bajo `_switchLock` (rechaza la petición si la sesión está `Stopped`, `Failed` o `Released`), crea la nueva fuente. Las capturas por aplicación se piden a la frecuencia del stream; si un dispositivo tiene otra frecuencia de mezcla, devuelve `false` sin cambiar nada (tampoco el nombre de la fuente activa) y `MainViewModel.ApplySourceAsync` detiene y reinicia la transmisión (única reconexión prevista).
2. **La fuente nueva se activa antes de engancharla, sin hueco**: `Start()` (aquí ocurre la activación de process loopback, que puede tardar) corre mientras la antigua sigue alimentando el stream. Si la activación falla, no ha cambiado nada: se libera la nueva, la antigua sigue sonando y el error llega a la App («No se pudo cambiar a …»).
3. Bajo `_sourceLock`, en un solo paso: desengancha `OnPcm` de la antigua, lo engancha a la nueva y la publica. Después libera la antigua.
4. `ReleaseGuard()` restaura la salida anterior y `ApplyGuard()` silencia la de la nueva fuente.
5. Escribe `# fuente cambiada a: …` en el CSV y lanza `SourceChanged`.

El servidor y la conexión HTTP no cambian: 0 reconexiones (autotest, comprobaciones 3 y 9). Si la aplicación se cierra, la sesión sigue enviando silencio del marcapasos y cada 3 s (`MonitorAsync`, `tick % 3`) busca el mismo ejecutable (`AudioApps.FindByExe`); si vuelve a tener audio, cambia a su nuevo PID. Si esa nueva captura falla, lo anota (`# … no se pudo volver a capturar «…»`) y lo reintenta en la siguiente comprobación.

## 5. Espectro y niveles (`Core/Audio/SpectrumAnalyzer.cs`)

| Parámetro | Valor |
|---|---|
| FFT | 2048 puntos (`FftOrder = 11`), ventana Hann, salto 1024 (50 % de solape) |
| Entrada | lo que realmente se envía (tras la pausa), mezclado a mono `(L+R)/2` |
| Bandas | 48, logarítmicas, 40 Hz – 16 kHz |
| Escala | −80 dB → 0, −12 dB → 1 |
| Lectores | `Snapshot()` (ventana principal, cada fotograma) y `PeekLevel()` (mini reproductor, cada 66 ms ≈ 15 fps); picos independientes |

Se ejecuta en el hilo de captura; los lectores copian bajo `lock`.

## 6. Servidor WAV y margen anti-cortes (`Core/Net/WavStreamServer.cs`)

- **Cabecera** WAV de 44 B con tamaños al máximo (RIFF `0xFFFFFFFF`, `data` `0xFFFFFFFF − 36`): ≈ 4 GiB ≈ **6,2 h** a 192 kB/s (`BuildWavHeader`).
- **Historial (margen)**: anillo de `AverageBytesPerSecond × PrebufferMs / 1000` bytes alineado a la trama (constructor): 192 000 B por segundo de margen a 48 kHz. Opciones de Ajustes: 0 / 1000 (por defecto) / 2000 / 3000 ms. Cada `Write` lo actualiza, haya o no clientes.
- **Relleno inicial**: al conectar, el cliente recibe de una vez el margen completo; si el anillo aún no está lleno (el Sonos se conecta 0,2–0,5 s después de empezar, ya durante `SetAVTransportURI`), lo que falta va **delante como silencio** (`HistorySnapshot`, `:114-122`). El margen y el directo se encolan bajo el mismo `lock`: sin hueco ni duplicado (prueba automática). Cada conexión nueva recibe su propio margen.
- **Cola por cliente** (`HandleClientAsync`): `Channel.CreateBounded<byte[]>(200)`, `SingleReader`, `FullMode = DropWrite`. El límite es de **200 trozos**, no de tiempo: ~2 s con trozos de 10 ms (captura por aplicación, marcapasos) y más con paquetes de dispositivo mayores (ráfagas de ~50 ms en la salida completa); el margen cuenta como un solo trozo. Con la cola llena el trozo nuevo se descarta y suma `DroppedChunks`: el Sonos recibiría un salto.
- `Write` nunca bloquea el hilo de audio: copia el bloque una vez (`ToArray`) y hace `TryWrite` en cada cola. Envío con `NoDelay = true`.
- **Métrica de cola** (`backlog_ms`): `PendingBytes` = bytes encolados para un cliente y aún no escritos en su socket. `TakeMaxBacklogMs()` devuelve el máximo desde la llamada anterior y lo reinicia al pendiente actual. En reproducción normal ≈ 60 ms (CSV real); crece solo cuando el Sonos o la red dejan de leer más allá de los búferes TCP de Windows (~1 s en loopback según la prueba).
- **Progreso por cliente** (`SinceLastClientProgress`): cada escritura completada en el socket anota la hora (`LastProgressTicks`, `Environment.TickCount64`); la propiedad devuelve el tiempo desde el progreso más reciente de cualquier cliente (0 sin clientes). Solo crece si la red o el Sonos dejan de leer, **nunca por silencio**, que se envía como cualquier otro audio. Lo usa `PlaybackWatchdog` ([NETWORKING.md](NETWORKING.md) §7).
- **Cierre forzado** (`AbortClients`): cada cliente tiene su `CancellationTokenSource` enlazado al del servidor; cancelarlo libera también una escritura bloqueada en el socket. Se cancela fuera del `lock` (las devoluciones de la cancelación pueden ejecutarse en línea) y un cliente que acaba de terminar se ignora. La desconexión queda como `desconexión … tras N s: cerrada por el vigilante (no recogía audio)` (al parar Audiora el motivo es `detenido`). Se usa antes de cada reintento, para que una conexión atascada no siga contando como cliente.
- Bucle de aceptación: ante una `SocketException` transitoria (p. ej. el adaptador se reconfigura) espera 100 ms antes de seguir, en lugar de girar en vacío.

## 7. Silencio local — `Core/Audio/LocalOutputGuard.cs`

| Paso | Detalle |
|---|---|
| Antes de tocar nada | escribe `{DeviceId, WasMuted, PreviousVolume, VolumeChanged}` en `output-guard.json` |
| Aplicar | primero `Mute = true`, después (solo con «Nivel completo») volumen al 100 %: nunca suena más alto |
| Restaurar (`Dispose`) | primero baja al volumen anterior, después restaura el silencio previo; borra el archivo |
| Tras un cierre inesperado | `RecoverPending` al arrancar la App: restaura y borra el archivo; si el dispositivo ya no existe, solo lo borra |

Qué dispositivo se silencia (`StreamingSession.ApplyGuard`): en «Todo el PC», la salida capturada (con «Nivel completo» opcional, porque esa captura es post-volumen); en «Una aplicación», la salida por la que suena la app, **siempre sin «Nivel completo»** y sin tocar el volumen maestro. Con «Silenciar el PC» desactivado no se crea guardia. La sesión restaura la guardia antes que cualquier otra cosa al detenerse (`TearDownAsync`), que se ejecuta siempre, termine como termine la vigilancia, y **por pasos**: guardia → captura → servidor (puerto) → cliente SOAP → dispositivo → CSV; si un paso falla se anota `limpieza: <excepción>` en `app-events.log` y los siguientes se ejecutan igual.

## 8. Atascos de red — `Core/Streaming/StallTracker.cs`

Cota inferior, no detector de cortes: los búferes TCP de Windows absorben los atascos cortos.

| Constante | Valor | Uso |
|---|---|---|
| Arranque ignorado | 3 s (`WarmupSeconds`) | cabecera, ráfaga del margen, arranque del Sonos; `RestartWarmup(elapsed)` lo vuelve a aplicar tras cada reintento y cierra el episodio en curso, para que el reintento y su ráfaga no cuenten como atasco |
| `BacklogThresholdMs` | 300 ms | `backlog_ms > 300` → atasco de ese tamaño |
| `LowRateFraction` | 0,6 | ritmo < 60 % del esperado (192 kB/s a 48 kHz) → atasco de `(1 − ritmo/esperado) × 1000` ms |

Solo cuenta con ≥ 1 cliente. Segundos malos seguidos = un episodio. Expone `Count`, `WorstMs` y `OverMarginCount` (episodios que superan el margen: probable corte audible; la App los muestra en naranja). Cada episodio nuevo escribe `# HH:mm:ss.fff atasco de red: ~N ms sin entregar (margen M ms, cola B ms, R kB/s)` en el CSV y `red: atasco de red: …` en `app-events.log`.

## 9. `stream-*.csv` (`StreamingSession.OpenLog` / `WriteLog`)

Una fila por vuelta de `MonitorAsync` (1 s más la respuesta del Sonos, cada consulta acotada a 2 s). Primera línea: `# target=<sala> <modelo> <IP>; capture=<fuente>; <formato de origen>; margen=<ms> ms`. Líneas `#` intercaladas con hora: conexión/desconexión del Sonos (puerto, método, ruta, `Range`, User-Agent, duración y motivo), atascos, cambios de fuente y vigilante (`vigilante: NotConsuming (Sonos PLAYING, clientes 1)`, `vigilante: reproducción correcta`, `reintento N/8 (<veredicto>, Sonos <estado>)`, `fin: …`). Escriben en el archivo el monitor, los eventos de conexión del servidor y las notas, desde hilos distintos: el escritor es `TextWriter.Synchronized` (con `AutoFlush`), así que las líneas no se mezclan.

| Columna | Significado |
|---|---|
| `elapsed_s` | segundos desde que el Sonos aceptó `Play` |
| `state` | `Streaming` o `Recovering` (la última fila se escribe antes del paso a `Failed` o `Released`) |
| `transport` | `CurrentTransportState` del Sonos o `UNREACHABLE` si `GetTransportInfo` falló o tardó más de 2 s |
| `clients` | conexiones HTTP abiertas |
| `send_rate_kBps` | bytes escritos en sockets desde la fila anterior ÷ 1000 ÷ **intervalo real medido** entre ambas lecturas (mínimo 0,5 s) |
| `bytes_sent` | acumulado (todas las conexiones) |
| `silence_kB` | relleno del marcapasos de la fuente actual (se reinicia al cambiar de fuente; no incluye la pausa) |
| `dropped_chunks` | trozos descartados por cola llena (acumulado) |
| `reconnects` | veces que se reenvió `SetAVTransportURI` + `Play` |
| `buffer_est_s` | **estimación ±1 s**: audio entregado desde antes de `SetAVTransportURI` − `RelTime` del Sonos; vacío si la posición no se pudo leer (solo se pide en `PLAYING`/`TRANSITIONING`) |
| `error` | último error (comas → `;`); «El Sonos no responde en la red» se borra en cuanto vuelve a responder |
| `source` | nombre de la fuente |
| `paused` | 1 en pausa |
| `peak_dbfs` | pico de lo enviado desde la fila anterior; vacío si −∞ |
| `backlog_ms` | máximo de audio esperando a que el Sonos lo recoja en ese intervalo |
