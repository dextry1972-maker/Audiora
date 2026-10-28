# Audiora — Pruebas

Qué se prueba, con qué y qué necesita cada prueba (código confirmado en `a7e2b36`, 28/10/2026). Reglas de seguridad en §6: se aplican a cualquier prueba que toque audio o el Sonos.

## 1. Niveles

| Nivel | Herramienta | Necesita | Qué cubre |
|---|---|---|---|
| **Automática** | `dotnet test` | nada (sockets en 127.0.0.1, carpetas temporales, reloj simulado) | servidor WAV, detector de atascos, vigilante de reproducción, política de reintentos, motor de copias |
| **Simulada** (tubería local) | `SonosFlow.CaptureCheck` | una salida de audio que nadie escuche (por defecto «Steam Streaming») | aislamiento de la captura, marcapasos, servidor y un lector local que hace de Sonos |
| **Con el Sonos físico** | `SonosFlow.exe --selftest-session`, `--autostart --screenshot` | el Sonos libre y en la red, puerto 8780 libre | sesión completa, cambio de fuente, pausa, silencio local, y (parte C) `Released` ante pausa o `Stop` repetido desde el Sonos y una reemisión ante un `Stop` aislado. La recuperación de un corte de red real no se automatiza |

Sin prueba automática: `LoopbackSource`, `ProcessLoopbackCapture`, `StreamingSession` (su lógica de vigilancia y reintentos sí: `PlaybackWatchdog` y `RecoveryPolicy`), `SonosClient`/`SonosDiscovery`, `LocalOutputGuard`, view models (incluida la espera de arranque), HUD e instalador. Se cubren con CaptureCheck, el autotest y las capturas de pantalla.

## 2. Compilar y pruebas automáticas

```powershell
dotnet build SonosFlow.sln -c Release
dotnet test SonosFlow.sln -c Release
```

`poc/` no está en la solución y no se compila ni se prueba con estos comandos. Instalador: pasos de `build.ps1` (sin `-ExecutionPolicy Bypass`).

| Proyecto | Pruebas | Qué comprueba |
|---|---|---|
| `Core.Tests/WavStreamServerTests` (7) | `Without_margin…` | sin margen, el audio anterior a la conexión no se envía |
| | `Margin_sends_exactly_the_last_ms…` | con 500 ms: empieza exactamente 500 ms antes del «ahora» y sigue sin hueco ni duplicado (contador por trama) |
| | `Margin_before_much_audio…` | margen de 2 s con 100 ms capturados: 1,9 s de ceros delante y luego el audio |
| | `Backlog_grows…` | lector parado 3 s → `backlog > 1000 ms`; al ponerse al día, < 50 ms |
| | `A_player_that_stops_reading…` | mientras lee, `SinceLastClientProgress` < 0,5 s; parado, > 1 s; `AbortClients` libera la escritura bloqueada, deja 0 clientes y la desconexión dice «vigilante» |
| | `Connections_and_disconnections…` | eventos `conexión …GET /sonosflow.wav` y `desconexión …` |
| | `Peers_outside_the_allow_list…` | IP fuera de la lista blanca → rechazada, 0 clientes |
| `Core.Tests/StallTrackerTests` (5) | | 600 s normales sin atascos; arranque (3 s) ignorado; segundos malos seguidos = un episodio, peor valor y «sobre el margen»; tras `RestartWarmup` el reintento y su ráfaga no cuentan; sin cliente no cuenta |
| `Core.Tests/PlaybackWatchdogTests` (10 + 1 teoría × 4 casos = 14) | | 1 h sana (el silencio no es fallo); posición parada → `Frozen` a los 8 s; posición desconocida nunca es `Frozen`, tampoco si las lecturas fallan después de armarse; `UNREACHABLE` con audio fluyendo se tolera 30 s y luego `NotPlaying`; `UNREACHABLE` con el audio parado o sin clientes → `NotPlaying` al momento; sin recoger audio → `NotConsuming` a margen + 3 s (5 s con 2 s); el arranque tras `Reset` oculta señales viejas; `STOPPED`/`PAUSED_PLAYBACK`/0 clientes → `NotPlaying` al momento; `TRANSITIONING` > 15 s → `NotPlaying`; `IsOurStream` con nuestra URI, vacía, Spotify y radio |
| `Core.Tests/RecoveryPolicyTests` (4) | | fallos < 5 s no reconectan; intentos a los 5, 10, 20, 40, 80, 140, 200, 260 s y abandono a los 320 s (el 8.º recibe sus 60 s); una vez abandonado sigue abandonado, nunca después de 600 s; 30 s sano reinicia el contador |
| `Backup.Tests/BackupServiceTests` (12) | | copia sin `bin/obj/artifacts`; nunca sobrescribe; secretos omitidos y listados; `verify` detecta alterados/faltantes/esenciales; `plan` no toca nada; `restore` copia antes el estado actual, conserva extras, admite carpeta aislada y rechaza copias dañadas; todo queda en el registro; rechaza carpeta de copias dentro del proyecto; lee el manifiesto antiguo de PowerShell |

Total (contado en los archivos de `a7e2b36`): **42 casos** = 30 de Core (26 `[Fact]` + 1 `[Theory]` con 4 `[InlineData]`) + 12 de copias (`[Fact]`); coincide con el «42/42» del commit. Cada prueba de copias usa `%TEMP%\sf-tests-<id>` y lo borra.

## 3. CaptureCheck (simulada, sin sonido audible)

`tools\SonosFlow.CaptureCheck\bin\Release\net8.0-windows\SonosFlow.CaptureCheck.exe <modo>` (`Program.cs`, `SoakPipe.cs`). Los tonos suenan en la salida «Steam Streaming» (configurable solo en el modo por defecto).

| Modo | Qué hace | Éxito |
|---|---|---|
| *(sin argumentos)* `[--device X]` | Dos procesos hijos: tono A 1 kHz y B 3,15 kHz a −12 dBFS. 1) capturar A: llega A (> −20 dBFS) y no B (< −60); 2) capturar B: al revés; 3) dispositivo completo: ambos (> −30); 4) informa si silenciar la sesión de A en el mezclador corta su captura; 5) si la app se cierra, la captura sigue sin error | `RESULTADO: todas las comprobaciones superadas`, código 0 |
| `list` | Lista las apps con audio (PID, nombre, ejecutable, activa) | — |
| `tone <hz> <s> <salida>` | Hijo: seno a −12 dBFS (ganancia 0,25) | — |
| `tone-quiet <hz> <s> <salida>` | Hijo: seno a **−60 dBFS** (ganancia 0,001). Su PID alimenta el autotest | — |
| `endpoint` | Silencia la salida de prueba y la baja al 10 %: la captura de la app no debe cambiar (Δ < 1 dB); muestra cuánto baja la del dispositivo. **Restaura** silencio y volumen de esa salida en `finally` | código 0 |
| `edge-isolation` | Edge sin ventana con el perfil aislado: árbol de procesos propio y reconocido como «YouTube Music (instancia aislada)» | 2 × `OK` |
| `soak <min> [--inject] [--margin ms]` | Seno 1 kHz → dos cadenas a la vez («Una aplicación» y «Todo el PC») → marcapasos → `WavStreamServer` en 127.0.0.1:8791/8792 → lector local. Detecta cualquier rotura de la onda a nivel de muestra; carga paralela de `AudioApps.List` cada 2,5 s. Informe cada 30 s | 0 cortes en ambas, código 0. Con `--inject` (corte deliberado de 300 ms a los 20 s) **debe** detectarse: código 1 esperado |
| `session <ip>` | Extremo a extremo con `StreamingSession` | No usable aquí: el firewall bloquea a CaptureCheck (ver §4) |

Referencias medidas: aislamiento A −12 / B −92 dBFS; resistencia 9 min sin margen y 7 min con 1 s: 0 cortes ([INVESTIGACION-CORTES-6MIN.md](INVESTIGACION-CORTES-6MIN.md)).

## 4. Autotest con el Sonos físico (`Infrastructure/SelfTest.cs`)

Requisitos: Sonos libre, ningún `SonosFlow` abierto, puerto 8780 libre, salida «Steam Streaming» (distinta de la predeterminada). El tono debe vivir hasta la comprobación 16 (~50 s + descubrimiento); con la parte C el autotest completo dura unos 2 min más.

```powershell
if (Get-Process SonosFlow -ErrorAction SilentlyContinue) { throw 'Audiora está abierto' }
if (Get-NetTCPConnection -LocalPort 8780 -State Listen -ErrorAction SilentlyContinue) { throw 'Puerto 8780 ocupado' }
$tone = Start-Process tools\SonosFlow.CaptureCheck\bin\Release\net8.0-windows\SonosFlow.CaptureCheck.exe -ArgumentList 'tone-quiet','200','120','"Steam Streaming"' -PassThru -WindowStyle Hidden
Start-Process src\SonosFlow.App\bin\Release\net8.0-windows10.0.19041.0\SonosFlow.exe -ArgumentList '--selftest-session',$tone.Id,'--selftest-out',"$env:TEMP\selftest.txt" -Wait
Get-Content "$env:TEMP\selftest.txt"
```

Usa los ajustes reales (margen, puerto) **sin guardarlos**, no toma el mutex ni los atajos y sale siempre con `QuitAsync` (detiene el stream y restaura el PC). La parte A transmite «Todo el PC» al Sonos: lo que suene en el PC se oirá. No existe la comprobación 8.

| # | Comprobación | Criterio |
|---|---|---|
| 1 | «Todo el PC», sin silenciar el PC | tras 7 s: `PLAYING`, 1 cliente |
| 1a | Margen anti-cortes | búfer medio (6 lecturas) > 0,4 + 0,6 × margen (s) y cola máx. < 500 ms |
| 1b | Mini reproductor | visible, `Topmost` según ajuste, `WS_EX_NOACTIVATE` |
| 1c | Mostrar/ocultar ventanas | 0 reconexiones, 1 cliente, `PLAYING`, bytes crecen |
| 1d / 5b / 9b | Una sola captura | `LoopbackSource.ActiveCount == 1` |
| 2 | La app del tono aparece | su proceso raíz está en la lista |
| 3 | Cambio en caliente a la app | 0 reconexiones, 1 cliente, > 150 kB/s |
| 4 | Llega solo el tono | pico −66…−54 dBFS, fuente = la app |
| 5 | Ventana y mini comparten estado | misma fuente, «Transmitiendo audio» |
| 6 / 7 | Pausa / reanudar | pausa: −∞ dBFS, > 150 kB/s, 1 cliente, «En pausa»; reanudar: pico > −66 |
| 9 | Vuelta a «Todo el PC» | 0 reconexiones, `PLAYING` |
| 10 | Detener | sin sesión, 0 capturas, puerto 8780 libre |
| 11 | Parte B: salida de la app | detectada y distinta de la predeterminada |
| 12–14 | «Una aplicación» + «Silenciar el PC» + «Nivel completo» (debe ignorarse) | `PLAYING`; salida de la app silenciada; el Sonos recibe −66…−54 dBFS |
| 15 | Volumen maestro intacto | predeterminada: volumen (±0,5 %) y silencio sin cambios; salida de la app: volumen sin cambios |
| 16 | Se mata el proceso del tono | `SourceAvailable = false`, sigue 1 cliente |
| 17 | Detener | salida de la app y predeterminada como antes; 0 capturas |
| 18 | Parte C: `Pause` enviado al Sonos tras 8 s de «Todo el PC» | en ≤ 20 s Audiora se retira (estado con «pausó»); 6 s después el Sonos sigue en `PAUSED_PLAYBACK` (no se reemitió) |
| 19 | Un `Stop` aislado | se trata como fallo de red: en ≤ 25 s, 1 reconexión y `PLAYING` |
| 20 | `Stop` otra vez 3 s después del reintento | en ≤ 25 s Audiora se retira (estado con «detuvo»); 6 s después el Sonos sigue en `STOPPED` |
| 21 | Nada queda activo | 0 capturas, puerto 8780 libre |

La parte C (si hay altavoz seleccionado) simula al usuario en la app de Sonos con un cliente SOAP propio que solo envía **órdenes de transporte** (`Pause`, `Stop`), nunca volumen ni ecualizador. Al terminar, el Sonos queda detenido (`STOPPED`).

Resultado: 26 líneas `OK`/`FALLO` + `RESULTADO: todo correcto` o `RESULTADO: N fallos`. Última ejecución: **26/26** (28/10/2026, 00:12–00:15, rama `mejoras/hitos-arquitectura` con el hito 2 integrado), incluida la parte C con el Sonos Five real: pausa → «Se pausó desde la app de Sonos» a los 5 s; Stop aislado → 1 reemisión, reproduciendo en 1 s; Stop repetido → «Se detuvo desde la app de Sonos». Si el Sonos rechazara `Pause`, la parte C se registra como `FALLO C. …` sin perder las comprobaciones anteriores.

La app se compila en `src\SonosFlow.App\bin\Release\net8.0-windows10.0.19041.0\` desde el hito 2 (TFM con la proyección de Windows para GSMTC); `CaptureCheck` sigue en `net8.0-windows`.

## 5. Capturas de pantalla

| Orden | Resultado |
|---|---|
| `SonosFlow.exe --screenshot out.png [--page settings] [--size 1100x760] [--delay 1500]` | PNG de la ventana principal y salida. Sin mutex, bandeja ni atajos; ajustes sin guardar. **No reanuda** una transmisión aunque «reanudar al iniciar» esté activo (`allowResume: false`) |
| `… --autostart --screenshot out.png --delay 15000` | Igual, pero **transmite de verdad** al Sonos (solo `--autostart` inicia) con los ajustes del usuario (también «Silenciar el PC»: el PC queda silenciado durante la prueba y se restaura al salir). Revisar después `stream-*.csv` y `app-events.log`. Si el Sonos no aparece, la espera de arranque reintenta **hasta 5 min** antes de la captura (`arranque: intento N…` en `app-events.log`) |
| `SonosFlow.exe --screenshot-mini mini.png [--expand] [--delay 1500]` | PNG del mini reproductor sobre fondo claro rayado (`#EEF1F4`); `--expand` abre la lista de fuentes. No reanuda transmisiones |
| `SonosFlow.exe --screenshot-hud hud.png` | **No inicializa** (sin descubrimiento ni Sonos): muestra el HUD con «Bedroom · 45 %» y barra al 45 %, espera 300 ms y guarda el PNG sobre fondo oscuro (`#2B3A4A`); no usa `--delay` |
| `--exit-after N` | Sale tras N s |

`--minimized` con `--screenshot` no funciona: la ventana oculta no se puede capturar y solo queda `captura fallida (¿ventana oculta?)` en `app-events.log`.

## 6. Reglas de seguridad (CLAUDE.md)

- **Nunca dejar el PC silenciado**: al terminar, comprobar `AudioEndpointVolume.Mute` de la salida predeterminada y que no queda `%LOCALAPPDATA%\SonosFlow\output-guard.json` (si existe, Audiora no restauró y lo hará al próximo arranque).
- No cambiar volumen ni ecualizador del Sonos, la red ni el firewall para probar. El autotest solo envía órdenes de transporte (`SetAVTransportURI`, `Play`, `Pause`, `Stop`).
- **Abortar si el puerto 8780 está ocupado o `SonosFlow` está abierto**: los modos de prueba no toman el mutex y comparten puerto, `output-guard.json` y carpeta de registros con la copia del usuario.
- No combinar `--minimized` con `--screenshot`.
- CaptureCheck no puede escuchar en red (firewall): las pruebas con el Sonos van por `SonosFlow.exe`.
- `CaptureCheck endpoint` cambia el silencio y el volumen de la salida de prueba y los restaura; comprobarlo si se interrumpe.
- Antes de cambios de riesgo (Core, instalador, restauraciones): `sfbackup create "Antes de <cambio>"` y verificarla. No crear tareas programadas ni servicios.

## 7. Registros leídos desde Claude

Claude es una app empaquetada: lo que lanza puede escribir en una copia virtual (`%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\…`) que oculta los archivos reales al leerlos. Las copias de pruebas se renombraron `*.claude-tests.*` (reversible). Antes de concluir «ajustes intactos» o «sin registros», confirmar qué copia se está leyendo. Según las notas de la investigación, `%LOCALAPPDATA%\Programs\SonosFlow` no se virtualiza.

## 8. Lista de regresión

- [ ] `git status` / `git log --oneline -5`; copia con `sfbackup` si el cambio es de riesgo.
- [ ] `dotnet build SonosFlow.sln -c Release`: 0 errores.
- [ ] `dotnet test SonosFlow.sln -c Release`: 42/42.
- [ ] Captura o aplicaciones tocadas: `CaptureCheck` (por defecto) superado; `list` muestra las apps.
- [ ] Marcapasos, servidor o margen tocados: `soak 9` con 0 cortes en ambas cadenas; `soak 1 --inject` detecta el corte; `soak 7 --margin 1000` sin cortes.
- [ ] Silencio local tocado: `CaptureCheck endpoint` superado.
- [ ] Con el Sonos libre: autotest `RESULTADO: todo correcto` (27 comprobaciones, incluidas la 1e de idioma y la parte C).
- [ ] Transmisión real 15 s: en el CSV `PLAYING`, 1 cliente, primera fila hasta ~380 kB/s (ráfaga del margen) y luego ~192–196 kB/s, `dropped_chunks` 0, `reconnects` 0, `backlog_ms` ~60, `buffer_est_s` ≈ 1,0–1,4 con margen de 1 s; ninguna línea `vigilante:` distinta de «reproducción correcta»; línea `conexión … UA=… Sonos/…` en `app-events.log`.
- [ ] Capturas de la ventana (home y settings), del mini reproductor y del HUD revisadas.
- [ ] Vigilancia o reintentos tocados: pruebas de `PlaybackWatchdog`/`RecoveryPolicy` en verde y comprobaciones 18–20 del autotest correctas (`fin: Se pausó…` / `fin: Se detuvo…` en el CSV, sin `Stop` de Audiora).
- [ ] Al final: PC sin silenciar, sin `output-guard.json`, sin procesos `SonosFlow`/`CaptureCheck`, puerto 8780 libre, volumen y ecualizador del Sonos sin cambios (tras el autotest el Sonos queda en `STOPPED`).
- [ ] Informe con lo que cambió respecto al estado anterior y cualquier fallo, sin afirmar que funciona lo que no se ha comprobado.
