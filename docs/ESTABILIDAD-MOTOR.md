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
- ~~Hilo de captura de «Todo el PC» con prioridad distinta al marcapasos~~ → **medido, ver abajo**: la prioridad es distinta (confirmado); que eso cause ceros delante de audio tardío **no** está demostrado (0 rellenos sospechosos en los registros).
- Pausas de 15,6 ms del temporizador de Windows (`Thread.Sleep(10)`).

**Medido (`CaptureCheck threads`, silencioso, 31/10/2026):** el hilo que entrega el audio de «Todo el PC» (`WasapiLoopbackCapture` de NAudio) no tiene nombre, es de prioridad **Normal (nativa 0)** y no es del pool (≈ 16 bloques/s); el de «Una aplicación» (`ProcessLoopback-<pid>`) es AboveNormal gestionada y **nativa 15 (tiempo crítico, MMCSS «Pro Audio»)** (≈ 100 bloques/s); el marcapasos (`SilencePacer`) es AboveNormal por código. Es decir, en «Todo el PC» el marcapasos puede adelantarse al hilo de captura bajo carga de CPU. Mitigación candidata (no aplicada, sin evidencia de daño): registrar el hilo de captura en MMCSS desde su primera devolución de llamada, como ya hace la captura por aplicación; se decidiría si `relleno_sospechoso` aparece en sesiones reales.

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

**Lectura honesta:** equivalentes. Las diferencias de milisegundos están dentro del ruido de planificación del sistema (una sola medición de cada una). Este soak **no ejercita el recorte de cola** (no hay atascos en él) ni el diagnóstico (no usa `StreamingSession`): solo demuestra que el cambio **no empeora la ruta normal**. Que la política nueva mejora la recuperación frente a un Sonos real **no está demostrado**: §1.4 lo mide con un modelo de reproductor a ritmo real y las pruebas (§3) prueban su mecánica; queda por medir con el Sonos (§4). No se inventa ninguna mejora porcentual.

### 1.4 Atasco inyectado en la cadena real de captura (simulado, silencioso)
`SonosFlow.CaptureCheck stall --seconds 8 [--maxpending-ms N] [--line-speed]` (≈ 1 min): seno por «Steam Streaming» → captura real (por aplicación y de dispositivo) → marcapasos → servidor → un «reproductor» local que **deja de leer N segundos** y vuelve, con el diagnóstico real conectado. El reproductor consume **a ritmo real** (mantiene ≈ 1 s de búfer, no puede ir más rápido que el reloj y no avanza si no tiene audio), como un Sonos: lo que queda por delante del punto de reproducción es **latencia que se queda**. `--maxpending-ms 60000` emula la cola anterior (sin límite útil). **Es un modelo de reproductor, no un Sonos.**

**Métrica:** latencia extremo a extremo *del modelo* = audio producido − audio descartado por el servidor − audio ya reproducido. Incluye la cola del servidor, los búferes TCP de ambos lados y el búfer del propio reproductor; solo la **diferencia** entre antes y 25 s después del atasco significa algo (la constante es el inicio del flujo). No es una medida del Sonos.

| Atasco de 8 s — latencia añadida que queda 25 s después | Por aplicación | Todo el PC |
|---|---|---|
| **Política nueva** (n = 5) | **2126 ms** (sd 5; 2117–2131) | **2313 ms** (sd 68; 2189–2397) |
| **Anterior emulada** (n = 5) | **7010 ms** (sd 8; 7000–7019) | **7002 ms** (sd 24; 6974–7035) |
| Atasco de 3 s, política nueva (n = 1) | +2005 ms | +1981 ms |

Descartes con la política nueva: 488 / 92–95 trozos (≈ 4,9 s de audio, contados); con la anterior, 0. Cola máxima del servidor: 2010–2030 ms (límite 2000 ms) frente a 6670–6900 ms.

**Lectura:** la política nueva reduce la latencia que se queda tras un atasco largo en ≈ 4,8 s (de ≈ 7,0 s a ≈ 2,1–2,3 s, ~70 %), con una dispersión de 5–68 ms entre las 5 ejecuciones por caso (frente a una diferencia de ≈ 4,7 s), a cambio de **perder ≈ 4,9 s de contenido** (un salto). **No elimina la latencia:** queda ≈ el límite de la cola (margen + 1 s) más búferes TCP. Y un hallazgo que no esperaba: **incluso un atasco corto de 3 s, sin descartes, deja ≈ +2 s permanentes** en este modelo (un reproductor a ritmo real no puede adelantarse). Ninguna de las dos políticas «se pone al día». Recuperar ese retraso exigiría un mecanismo explícito (p. ej. recortar audio hasta volver al margen) que **no se implementa**: sin evidencia con un Sonos real de que el retraso persista y moleste, sería un cambio especulativo que añade saltos audibles.

**Correcciones a mi propio trabajo (para que no se repitan los errores):** (1) una primera medida con el lector a velocidad de línea mostraba «+4,9 s de retraso» con la política nueva y 0 con la anterior: era una métrica errónea (medía contenido no recibido; un lector así recupera todo lo retenido, algo que un Sonos no puede hacer). (2) La segunda, «cola que queda en el servidor» (10 / 310 ms con la nueva), solo contaba la cola del servidor y dejaba fuera los búferes TCP y el del reproductor: **sobreestimaba la mejora** (la revisión independiente lo detectó). Sustituida por la métrica extremo a extremo de arriba.

**Límites:** (a) el seno de 1 kHz y los trozos de 10/50 ms (múltiplos enteros de su periodo) hacen que **el empalme del descarte no se vea como discontinuidad** (0 cortes detectados); con música puede haber un clic; (b) no se sabe qué hace un Sonos real tras un atasco largo (puede cortar la conexión, como el 28/10) ni su búfer interno; (c) 5 repeticiones por caso para el atasco de 8 s (1 para el de 3 s), todas en el mismo equipo y la misma sesión de trabajo: la dispersión es pequeña pero no cubre otras máquinas, cargas ni dispositivos; (d) la latencia absoluta del modelo no es la del Sonos.

### 1.5 Autotest real con el Sonos (31/10/2026 02:23, autorizado por el usuario)
`SonosFlow.exe --selftest-session <pid del tono −60 dBFS>` con la compilación de esta rama (`0b2f3a4`), Audiora instalada y navegadores cerrados, puerto 8780 libre: **35 de 35 comprobaciones correctas, «todo correcto»** (sesión completa con el Sonos real: transmisión, cambio de fuente en caliente, pausa, silencio local, pausa/Stop desde la app de Sonos, reemisión tras un Stop aislado, restauración de la salida; ~2 min 20 s). Sin cortes de audio audibles evaluados (era un tono de −60 dBFS: **no es una prueba acústica**). Nada quedó silenciado y no se cambió ningún ajuste del usuario (el volumen de la salida predeterminada ya estaba al 66 % al empezar y el autotest lo registró idéntico al terminar).

**Primera ejecución real del diagnóstico** (5 sesiones del autotest → 5 `diag-*.jsonl` en los registros reales, 1–2 KB cada uno): todas las líneas son JSON válido, 0 con direcciones IP, ciclo completo `inicio → http_abierta → … → resumen10s → http_cerrada → resumen`, y los veredictos llegan al registro de eventos («diagnóstico: … / diagnóstico detallado: diag-…»). Esto confirma con hardware lo que antes solo estaba probado contra un Sonos falso.

**Lo que encontró (y corregí):** el episodio `cola 1010 ms` a los 3,2 s de la primera sesión era el **margen de 1 s entregado de golpe al conectar el Sonos**, no un atasco: un falso positivo del propio diagnóstico. Ahora, durante los 8 s posteriores a abrirse la conexión, el margen no cuenta como cola anómala (el umbral sube en `margen` ms); una cola real mayor sigue marcándose (prueba nueva). **Sin repetir el autotest tras la corrección** (la autorización fue para una ejecución): la corrección está probada por pruebas unitarias, no con el Sonos.

**Cómo leer los demás episodios:** los `relleno_sospechoso` (779/1016/126 ms a los 36–38 s de la sesión 1 y 26 ms en la 3) caen en el tramo de cambios de fuente, pausa y silencio local del autotest, donde el audio capturado se interrumpe a propósito; el clasificador es una **heurística** (relleno del marcapasos en los 2 s siguientes a audio audible) y no distingue una interrupción deliberada de un fallo de captura. Que coincidan con esos pasos es lo esperable, pero **no lo he verificado paso a paso**.

**Límites:** una sola ejecución de ~2 min; no es una sesión larga, ni con corte de hotspot, ni con Wi-Fi convencional, ni acústica; el tono es de −60 dBFS.

## 2. Qué se implementó (y por qué)

| Fase | Decisión |
|---|---|
| 2 Diagnóstico | **Hecho.** `Core/Diagnostics/StreamDiagnostics.cs`: reloj monotónico (`Stopwatch`), intervalos de captura, relleno del marcapasos **clasificado** (legítimo / tras audio reciente), tiempo por bloque, duración de cada escritura HTTP, ciclo de vida de conexiones, descartes con bytes y ms, detector de episodios, `PeriodicityAnalyzer`, `DriftEstimator` (con su incertidumbre). Escribe `diag-*.jsonl` por sesión: eventos y resúmenes de 10 s; **sin audio, sin IP, sin nombres de aplicación**; tope de 1 MiB por archivo y 30 archivos; texto libre saneado y cortado a 300 caracteres. Solo mide. La latencia de extremo a extremo **no** se presenta como medida: el búfer del Sonos sigue siendo una estimación (±1 s). |
| 3 HTTP | Cabeceras auditadas y **sin cambios** (válidas para un flujo en vivo). Corregido: bucle de aceptación a prueba de excepciones (registra las 5 primeras y luego 1 de 60, con pausa, sin girar), fallos por conexión registrados en vez de sin observar, contadores por cliente en la desconexión. **No** se añadió `SO_KEEPALIVE`/`WriteTimeout`: sin evidencia de conexiones semiabiertas (el único cierre real vino con RST) — queda como siguiente paso si el diagnóstico las muestra. |
| 4 Gobernador de ritmo | **Investigado, no implementado.** Evidencia a favor: ninguna (0 huecos, 0 rellenos sospechosos, deriva sin medir aún). Las ráfagas de 540 kB/s observadas son el vaciado de la cola TCP tras un atasco (deseable). El marcapasos ya acota su contabilidad a ±120 ms. Para decidirlo con datos: `relleno_sospechoso` y `deriva` del diagnóstico. |
| 5 Búferes y *backpressure* | **Hecho.** Cola por reproductor acotada en **tiempo** (margen + 1 s, entre 1,5 y 4 s) con descarte explícito del audio **más antiguo**, contado y registrado. Así un reproductor que se atasca y se recupera retoma cerca del directo en vez de reproducir segundos de audio viejo. Consecuencia documentada: tras un atasco mayor que el límite se pierde ese tramo (un salto, posible clic en el empalme; el Sonos ya se había cortado). Con tráfico normal la cola vale ≈ un trozo y no se descarta nada (probado). Medido en §1.4 (modelo de reproductor a ritmo real, extremo a extremo): tras un atasco de 8 s la latencia añadida baja de ≈ 7,0 s (política anterior emulada) a ≈ 2,1–2,3 s; **no desaparece**. |
| 6 Windows (MMCSS, `IAudioClient3`, eventos) | **No cambiado.** La captura por aplicación ya usa eventos + MMCSS «Pro Audio». «Todo el PC» usa `WasapiLoopbackCapture` de NAudio (modo no verificado). Sin evidencia de que el problema sea de latencia o de planificación, no se toca la prioridad de hilos. |
| 7 Recuperación | Ya existía y se verificó por lectura (vigilante, 8 reintentos con retroceso 5–60 s, `Released`, no recrea servidor). Sin cambios de política: el episodio real se recuperó en ≈ 6 s tras la desconexión. No se recrea el servidor (hipótesis de escucha sorda sin confirmar). |
| 8 Formatos | Solo PCM WAV 48 kHz/16 bit/2 ch (192 kB/s); sin códecs que auditar ni añadir. |

## 3. Pruebas

Nuevas (47 en Core; Core total 112, App 176, Backup 12 = 300; Core repetido 3 veces seguidas sin fallos): periodicidad (patrón 300/330/360 s detectado; irregular y < 4 episodios no), deriva ± y sin deriva, episodios, diagnóstico (clasificación de rellenos, causas, archivo sin IP/audio, tope, poda, ubicación no escribible, hilos), cola por tiempo (paquetes de 50 ms, empalme, contabilidad exacta, audio normal sin descartes), apretón de manos abortado y tareas no observadas del servidor, peticiones basura, 60 reconexiones sin fugas, segunda conexión con la primera aún viva, flujo indefinido **ACELERADO** (6 min de *datos*, no de tiempo) y eventos de diagnóstico.

**Sesión completa contra un Sonos falso** (`StreamingSessionDiagnosticsTests`, 13 s): una `StreamingSession` real (captura WASAPI real de «Steam Streaming», marcapasos, servidor HTTP, vigilante y monitor de 1 Hz) contra un Sonos de pega en `127.0.0.1:1400` que contesta las llamadas UPnP de la sesión y descarga el flujo como un reproductor; a los 3 s el «reproductor» deja de leer 4 s y vuelve. Comprueba: el Sonos falso recibió `SetAVTransportURI` y `Play` y descargó el flujo; se crea **un** `diag-*.jsonl` con todas las líneas JSON válidas, `inicio` (modo), `resumen10s`, `resumen`, `http_abierta`, `http_cerrada`, **`episodio`** y **`descartes`** del atasco, sin IP ni nombre de sala y < 64 KB; la sesión informa `diagnóstico:` y `diagnóstico detallado: diag-…` y el CSV guarda el veredicto; y la sesión **no pasa a `Failed`** por el atasco. Esto ejecuta el enganche de `StreamingSession` (creación, Tick, Note, Finish y Dispose en el cierre). Sigue siendo un Sonos falso: no prueba nada del comportamiento del hardware. **Sin la salida «Steam Streaming» la prueba no hace nada** (termina en verde y escribe «OMITIDA» en su salida; xunit 2.5 no permite omitir en tiempo de ejecución): un equipo sin esa salida **no** ejecuta esta comprobación, salvo que se defina `AUDIORA_REQUIRE_SESSION_TEST=1`, que convierte la ausencia en un fallo. En este equipo se ejecutó con esa variable puesta (pasó en ≈ 17 s). Los tiempos son fijos (3 s + 4 s + 9 s) y el puerto 1400 del Sonos falso debe estar libre (y el 8795 de la sesión).

Distinguir: **simuladas/aceleradas** (las anteriores y el soak local) vs **reales** (los registros de uso de §1.1). Una simulación rápida no prueba por sí sola seis minutos de estabilidad: el soak de 7 min es tiempo real pero solo cubre el PC.
La prueba existente `Backlog_grows_while_the_player_stops_reading_and_recovers` se ajustó **a propósito** a la política nueva (ya no se retienen 20 s de audio: se descarta lo antiguo y se cuenta); tres pruebas mías fallaron primero por errores de la propia prueba (redondeo a enteros, margen incluido en el flujo, productor sin ritmo real) y se corrigieron sin tocar el código.

### Auditoría independiente (`audiora-code-reviewer`) del commit
Sin bloqueos en la cola nueva (sin pérdida de despertar, contabilidad consistente, alineación a `BlockAlign` conservada, sin giro apretado en la aceptación). Hallazgos y qué se hizo: **(media, real)** las notas del diagnóstico podían filtrar nombres de ejecutables/IP/rutas → `StreamDiagnostics.Sanitize` (nombres entre «», `.exe/.dll/.lnk`, IPv4, rutas, ids de dispositivo; 160 caracteres) y pruebas; **(media)** un fallo del diagnóstico podía tumbar el inicio de la sesión → constructor, escritura y `Tick` capturan cualquier excepción, números no finitos saneados; **(media)** colisión de nombre de archivo → milisegundos y sufijo; ms del relleno calculados con el formato real; aceptación sin punto ciego en `RemoteEndPoint`; `Dispose` garantizado en el cierre; límite de cola sin tope superior (margen + 1 s); pruebas menos dependientes de los búferes del socket (100 s de audio) y sin aserciones vacías. **No corregido por mínimo:** `IntervalStats.Take` no atómico (solo diagnóstico), `PendingBytes` inflado un instante durante el cierre (inofensivo). **Vigilar:** el atasco largo ya no se ve como cola de 10 s sino como cola acotada (≈ 2–4 s) + descartes + tasa de envío; el vigilante y `StallTracker` usan progreso y tasa, no colas > 4 s.

### Revisión de regresiones del lote de correcciones (`c8c57bd`)
Segunda revisión independiente, solo lectura: **sin regresiones graves ni bloqueantes**. Verificó el escape de las expresiones regulares (sin ReDoS ni falsos positivos sobre `5000ms`, `cliente#3`, `cola 1500ms`), que el saneado cubre todas las vías de texto libre hacia el archivo, `OpenFile` (hasta 5 nombres, `FilePath` solo si se abrió), el límite de cola para margen 0/1/2/3 s (1,5/2/3/4 s: siempre margen + 1 s, nunca < 1,5 s) y el orden `Finish → Dispose` del cierre. Tres defectos menores **corregidos**: el recorte de 160 caracteres cortaba justo los contadores finales de `http_cerrada` (ahora 300), el recorte podía partir un par sustituto y desactivar todo el archivo (ahora no), y `Sanitize(null)` lanzaría (ahora devuelve vacío); con pruebas. Observaciones sobre pruebas: `Two_sessions_in_the_same_millisecond…` valida el resultado pero no fuerza la colisión del milisegundo; el umbral de descartes de la prueba de 100 s depende de la velocidad de drenado del cliente (sin jitter observado en 6 ejecuciones del Core). No comprobado por el revisor: ejecución de pruebas, comportamiento bajo carga.

## 4. Validación pendiente (necesita autorización: toca el Sonos real)

1. ~~Autotest con el Sonos~~ **Hecho el 31/10 con autorización: 35/35** (ver §1.5). Conviene repetirlo tras la corrección del margen para confirmar que el falso `cola` desaparece.
2. **Sesión larga real (≥ 30 min) con hotspot y con Wi-Fi convencional**, margen 1 s y margen 0, anotando la hora de cada corte audible; después `python tools/analizar-sesiones.py` y el `diag-*.jsonl` correspondiente (episodios, `relleno_sospechoso`, `escritura_lenta`, descartes, periodicidad, deriva).
3. **Baja latencia frente a estable:** margen 0 / 1 s / 2 s en la misma red, comparando cortes oídos y `buffer_est`.
4. Corte controlado de Wi-Fi (apagar el hotspot unos segundos) para ver el recorte de cola y el tiempo de recuperación reales.

## 5. Archivos

Nuevos: `Core/Diagnostics/StreamDiagnostics.cs`, `StreamAnalysis.cs`; `tools/analizar-sesiones.py`; pruebas `StreamAnalysisTests.cs`, `StreamServerHardeningTests.cs`. Modificados: `Net/WavStreamServer.cs`, `Audio/LoopbackSource.cs`, `Streaming/StreamingSession.cs`, `WavStreamServerTests.cs`.
Volver al comportamiento anterior: `git checkout main` (nada instalado); la Fase 1 de Codex sigue intacta en su rama.
