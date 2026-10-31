# Estabilidad del motor de transmisión — diagnóstico y revisión

Rama `motor/estabilidad-diagnostico` (sobre `main` + la Fase 1 de Codex, sin fusionar ni instalar). 31/10/2026.
**No se declara resuelto ningún corte.** Se demuestra dónde falla, se corrige lo que el código y los datos justifican, y se deja medido lo que aún no se puede decidir.

## 1. Dónde falla: evidencia (no suposiciones)

### 1.1 Datos reales de uso (`%LOCALAPPDATA%\SonosFlow\logs`, 188 sesiones; 27 de ≥ 5 min)
Analizador: `python tools/analizar-sesiones.py 300` (solo lectura).

| Pregunta | Resultado |
|---|---|
| ¿Hay un patrón de 5–6 min? | **No.** Episodios anómalos escasos e irregulares: intervalos de 20 s a 88 min (p. ej. 152, 84, 62, 53, 314 s; 5316, 186 s). Ninguna sesión tiene ≥ 4 episodios con un intervalo dominante de 270–400 s. |
| ¿Falla la captura o el procesamiento en el PC? | **No en los registros:** 0 huecos de captura y 0 descartes al final de **todas** las sesiones largas. |
| ¿Hay reconexiones? | Una sola (28/10 17:21, ver abajo). |
| ¿Qué es lo que sí falla? | Un episodio grave y otro leve, **ambos del lado Sonos/Wi-Fi**. |
| Margen 0 ms | Búfer estimado del Sonos mediana 0,5–0,6 s, mínimo 0,0 s: cualquier microcorte de red es audible y **el registro de 1 s no lo ve**. Con 1 s: 0,8–2,0 s. |

**Episodio grave (28/10, sesión de 17 min, margen 1 s):** 17:38:08 el Sonos deja de leer (atasco 3050 ms, cola hasta 4810 ms, ritmo 10–540 kB/s), UPnP responde «inalcanzable», 17:38:22 el Sonos **cierra él la conexión** («forcibly closed by the remote host»), 17:38:28 el vigilante lanza el reintento 1/8 y a las 17:38:30 vuelve a sonar. ≈ 22 s de principio a fin; la recuperación existente funcionó.

**Episodios leves (sesión de 3 h):** 1:42:02 (cola 940 ms) y 1:45:08 (cola 1260 ms): absorbidos por el margen de 1 s; el búfer estimado vuelve a ≈ 1,3 s tras cada uno (**no queda latencia acumulada** para atascos ≤ margen).

### 1.2 Auditoría de código (dos revisiones independientes, solo lectura, + lectura propia)
**Confirmado en el código**
- El marcapasos (`LoopbackSource.PacerLoop`) rellena con ceros cualquier hueco de captura > 120 ms y `SilenceBytes` **no distingue** silencio legítimo de relleno tras audio: un fallo de captura se vería como «silencio».
- La cola por reproductor se limitaba en **200 trozos**, no en tiempo. Medido: la captura por aplicación entrega trozos de ≈ 10 ms (hueco máx. 12 ms) y la de «Todo el PC» de ≈ 50 ms (hueco máx. 52 ms) → la misma cola admitía ≈ 2 s o ≈ **10 s** de audio acumulado según el modo.
- El bucle de aceptación solo capturaba `SocketException`: cualquier otra excepción lo mataba **en silencio**; las excepciones al escribir cabeceras quedaban sin observar y sin registrar.
- No hay `WriteTimeout`/`SO_KEEPALIVE`: la única defensa ante un Sonos que desaparece es el vigilante (5–6 s).
- Cabeceras: `HTTP/1.1 200`, `Content-Type: audio/wav`, sin `Content-Length` ni `Transfer-Encoding` (válido: cuerpo hasta cerrar), `Accept-Ranges: none`, `Connection: close`, DLNA streaming. Cabecera WAV con tamaños 0xFFFFFFFF (≈ 6,2 h a 192 kB/s: no explica cortes a los 6 min). Range se ignora (200 completo). No hay temporizadores de ≈ 300/360 s ni suscripciones GENA en `Core`.

**Hipótesis respaldadas (sin confirmar)**
- Escucha ligada a una IP (192.168.137.1 del hotspot): si el adaptador la retira y la repone, el `TcpListener` podría quedar sordo sin excepción; el reintento solo reemite la URI.
- Deriva del reloj del dispositivo más lenta que el monotónico → relleno de ceros a ≈ 6 min con ~170–200 ppm (coincidencia numérica, sin evidencia; el soak de 7–9 min no la muestra).
- Hilo de captura de «Todo el PC» (NAudio) con prioridad distinta al marcapasos (AboveNormal): ceros delante de audio tardío.
- Pausas de 15,6 ms del temporizador de Windows (`Thread.Sleep(10)`).

**No investigado / no comprobable aquí:** búfer real y reglas internas del Sonos, comportamiento real ante cabecera WAV de tamaño máximo, DHCP/escaneo de radio del hotspot, `IAudioClient3` en el loopback (no se pudo verificar la API del dispositivo virtual de captura por proceso), código fuente de NAudio 2.2.1 (modo eventos/sondeo de «Todo el PC»).

### 1.3 Línea base reproducible (simulada, local, sin Sonos)
`SonosFlow.CaptureCheck soak 7 --margin 1000` (seno por «Steam Streaming», captura → marcapasos → servidor → lector local con detector por muestra). **Es una simulación acelerada de la ruta del PC, no prueba la estabilidad frente al Sonos real.**

| | Una aplicación | Todo el PC (dispositivo) |
|---|---|---|
| Antes (25408f8: `main` + Fase 1 de Codex) — cortes en la onda | **0** | **0** |
| Muestras / audio recibido | 20 268 000 (422,3 s en 422,5 s) | 20 209 440 (421,0 s en 422,5 s) |
| Relleno de silencio del marcapasos | 0 kB | 0 kB |
| Hueco máx. entre paquetes de captura | 11,7 ms | 51,9 ms |
| Bloqueo máx. de lectura (lado Sonos) | 15,3 ms | 52,8 ms |
| Descartes en el servidor | 0 | 0 |

Después (commit `bdd2587`, mismas condiciones, 7 min reales, 31/10 00:48):

| | Una aplicación | Todo el PC (dispositivo) |
|---|---|---|
| Cortes en la onda recibida | **0** | **0** |
| Audio recibido | 20 268 000 muestras (422,3 s en 422,5 s) | 20 210 880 (421,1 s en 422,5 s) |
| Relleno de silencio / descartes | 0 kB / 0 | 0 kB / 0 |
| Hueco máx. entre paquetes de captura | 11,7 ms | 55,9 ms (antes 51,9) |
| Bloqueo máx. de lectura | 12,6 ms (antes 15,3) | 56,3 ms (antes 52,8) |

**Lectura honesta:** equivalentes. Las diferencias de milisegundos están dentro del ruido de planificación del sistema (una sola medición de cada una). Este soak **no ejercita el recorte de cola** (no hay atascos en él) ni el diagnóstico (no usa `StreamingSession`): solo demuestra que el cambio **no empeora la ruta normal**. Que la política nueva mejora la recuperación tras un atasco real **no está demostrado**: solo se prueba su mecánica con pruebas (§3) y queda por medir con el Sonos (§4). No se inventa ninguna mejora porcentual.

## 2. Qué se implementó (y por qué)

| Fase | Decisión |
|---|---|
| 2 Diagnóstico | **Hecho.** `Core/Diagnostics/StreamDiagnostics.cs`: reloj monotónico (`Stopwatch`), intervalos de captura, relleno del marcapasos **clasificado** (legítimo / tras audio reciente), tiempo por bloque, duración de cada escritura HTTP, ciclo de vida de conexiones, descartes con bytes y ms, detector de episodios, `PeriodicityAnalyzer`, `DriftEstimator` (con su incertidumbre). Escribe `diag-*.jsonl` por sesión: eventos y resúmenes de 10 s; **sin audio, sin IP, sin nombres de aplicación**; tope de 1 MiB por archivo y 30 archivos. Solo mide. La latencia de extremo a extremo **no** se presenta como medida: el búfer del Sonos sigue siendo una estimación (±1 s). |
| 3 HTTP | Cabeceras auditadas y **sin cambios** (válidas para un flujo en vivo). Corregido: bucle de aceptación a prueba de excepciones (registra las 5 primeras y luego 1 de 60, con pausa, sin girar), fallos por conexión registrados en vez de sin observar, contadores por cliente en la desconexión. **No** se añadió `SO_KEEPALIVE`/`WriteTimeout`: sin evidencia de conexiones semiabiertas (el único cierre real vino con RST) — queda como siguiente paso si el diagnóstico las muestra. |
| 4 Gobernador de ritmo | **Investigado, no implementado.** Evidencia a favor: ninguna (0 huecos, 0 rellenos sospechosos, deriva sin medir aún). Las ráfagas de 540 kB/s observadas son el vaciado de la cola TCP tras un atasco (deseable). El marcapasos ya acota su contabilidad a ±120 ms. Para decidirlo con datos: `relleno_sospechoso` y `deriva` del diagnóstico. |
| 5 Búferes y *backpressure* | **Hecho.** Cola por reproductor acotada en **tiempo** (margen + 1 s, entre 1,5 y 4 s) con descarte explícito del audio **más antiguo**, contado y registrado. Así un reproductor que se atasca y se recupera retoma cerca del directo en vez de reproducir segundos de audio viejo. Consecuencia documentada: tras un atasco mayor que el límite se pierde ese tramo (un salto, posible clic en el empalme; el Sonos ya se había cortado). Con tráfico normal la cola vale ≈ un trozo y no se descarta nada (probado). |
| 6 Windows (MMCSS, `IAudioClient3`, eventos) | **No cambiado.** La captura por aplicación ya usa eventos + MMCSS «Pro Audio». «Todo el PC» usa `WasapiLoopbackCapture` de NAudio (modo no verificado). Sin evidencia de que el problema sea de latencia o de planificación, no se toca la prioridad de hilos. |
| 7 Recuperación | Ya existía y se verificó por lectura (vigilante, 8 reintentos con retroceso 5–60 s, `Released`, no recrea servidor). Sin cambios de política: el episodio real se recuperó en ≈ 6 s tras la desconexión. No se recrea el servidor (hipótesis de escucha sorda sin confirmar). |
| 8 Formatos | Solo PCM WAV 48 kHz/16 bit/2 ch (192 kB/s); sin códecs que auditar ni añadir. |

## 3. Pruebas

Nuevas (44 en Core; Core total 109, App 176, Backup 12 = 297; Core repetido 3 veces seguidas sin fallos): periodicidad (patrón 300/330/360 s detectado; irregular y < 4 episodios no), deriva ± y sin deriva, episodios, diagnóstico (clasificación de rellenos, causas, archivo sin IP/audio, tope, poda, ubicación no escribible, hilos), cola por tiempo (paquetes de 50 ms, empalme, contabilidad exacta, audio normal sin descartes), apretón de manos abortado y tareas no observadas del servidor, peticiones basura, 60 reconexiones sin fugas, segunda conexión con la primera aún viva, flujo indefinido **ACELERADO** (6 min de *datos*, no de tiempo) y eventos de diagnóstico.

Distinguir: **simuladas/aceleradas** (las anteriores y el soak local) vs **reales** (los registros de uso de §1.1). Una simulación rápida no prueba por sí sola seis minutos de estabilidad: el soak de 7 min es tiempo real pero solo cubre el PC.
La prueba existente `Backlog_grows_while_the_player_stops_reading_and_recovers` se ajustó **a propósito** a la política nueva (ya no se retienen 20 s de audio: se descarta lo antiguo y se cuenta); tres pruebas mías fallaron primero por errores de la propia prueba (redondeo a enteros, margen incluido en el flujo, productor sin ritmo real) y se corrigieron sin tocar el código.

### Auditoría independiente (`audiora-code-reviewer`) del commit
Sin bloqueos en la cola nueva (sin pérdida de despertar, contabilidad consistente, alineación a `BlockAlign` conservada, sin giro apretado en la aceptación). Hallazgos y qué se hizo: **(media, real)** las notas del diagnóstico podían filtrar nombres de ejecutables/IP/rutas → `StreamDiagnostics.Sanitize` (nombres entre «», `.exe/.dll/.lnk`, IPv4, rutas, ids de dispositivo; 160 caracteres) y pruebas; **(media)** un fallo del diagnóstico podía tumbar el inicio de la sesión → constructor, escritura y `Tick` capturan cualquier excepción, números no finitos saneados; **(media)** colisión de nombre de archivo → milisegundos y sufijo; ms del relleno calculados con el formato real; aceptación sin punto ciego en `RemoteEndPoint`; `Dispose` garantizado en el cierre; límite de cola sin tope superior (margen + 1 s); pruebas menos dependientes de los búferes del socket (100 s de audio) y sin aserciones vacías. **No corregido por mínimo:** `IntervalStats.Take` no atómico (solo diagnóstico), `PendingBytes` inflado un instante durante el cierre (inofensivo). **Vigilar:** el atasco largo ya no se ve como cola de 10 s sino como cola acotada (≈ 2–4 s) + descartes + tasa de envío; el vigilante y `StallTracker` usan progreso y tasa, no colas > 4 s.

## 4. Validación pendiente (necesita autorización: toca el Sonos real)

1. **Autotest con el Sonos** (`--selftest-session`, ~2–3 min, tono de −60 dBFS; requiere Audiora instalada cerrada, puerto 8780 libre, Chrome/Edge/Brave cerrados): comprueba que la sesión real crea `diag-*.jsonl` y que nada se rompe.
2. **Sesión larga real (≥ 30 min) con hotspot y con Wi-Fi convencional**, margen 1 s y margen 0, anotando la hora de cada corte audible; después `python tools/analizar-sesiones.py` y el `diag-*.jsonl` correspondiente (episodios, `relleno_sospechoso`, `escritura_lenta`, descartes, periodicidad, deriva).
3. **Baja latencia frente a estable:** margen 0 / 1 s / 2 s en la misma red, comparando cortes oídos y `buffer_est`.
4. Corte controlado de Wi-Fi (apagar el hotspot unos segundos) para ver el recorte de cola y el tiempo de recuperación reales.

## 5. Archivos

Nuevos: `Core/Diagnostics/StreamDiagnostics.cs`, `StreamAnalysis.cs`; `tools/analizar-sesiones.py`; pruebas `StreamAnalysisTests.cs`, `StreamServerHardeningTests.cs`. Modificados: `Net/WavStreamServer.cs`, `Audio/LoopbackSource.cs`, `Streaming/StreamingSession.cs`, `WavStreamServerTests.cs`.
Volver al comportamiento anterior: `git checkout main` (nada instalado); la Fase 1 de Codex sigue intacta en su rama.
