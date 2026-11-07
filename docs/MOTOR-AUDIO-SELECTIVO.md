# Motor de audio selectivo: una aplicación al Sonos sin tocar el resto del PC

Estado (28/10/2026, rama `motor/audio-selectivo`): implementado y medido con pruebas automáticas y con CaptureCheck; resultados de la prueba con el Sonos y de la prueba prolongada en la sección 6.

## 1. Arquitectura

```
App elegida (y sus procesos hijos)
  └─ captura por proceso (Application Loopback de Windows, INCLUDE_TARGET_PROCESS_TREE)
       48 kHz · 16 bit · 2 canales PCM pedidos a Windows (AUTOCONVERTPCM + SRC_DEFAULT_QUALITY), por eventos,
       hilo propio con MMCSS «Pro Audio»                                   ProcessLoopbackCapture.cs
  └─ LoopbackSource: conversión a 16 bit estéreo si hace falta y marcapasos que rellena silencio cuando la app calla
                                                                            LoopbackSource.cs
  └─ WavStreamServer: anillo acotado (margen anti-cortes), WAV infinito por HTTP solo en la IP local de la red del
       Sonos y solo para las IP de los Sonos descubiertos                   WavStreamServer.cs
  └─ Sonos: control local UPnP AVTransport (SetAVTransportURI + Play), vigilante y reintentos acotados
                                                                            SonosClient.cs, StreamingSession.cs
```

Esta cadena ya existía y se conserva: la captura en tiempo real nunca espera a la red (el hilo de captura solo copia
a una cola acotada; si el Sonos no recoge, se descarta y se cuenta en `dropped_chunks`), y el disco (CSV) va aparte.

### Formato: WAV PCM 16 bit, no MP3/AAC
- No es PCM flotante: Windows convierte lo que la app reproduzca a PCM entero de 16 bit y 48 kHz.
- Sonos admite WAV de 16 bit hasta 48 kHz ([soporte de Sonos](https://support.sonos.com/en/article/supported-audio-formats-for-sonos-music-library)); la lista de [docs.sonos.com](https://docs.sonos.com/docs/supported-audio-formats) no menciona WAV, pero el Sonos Five lo reproduce de forma continua y verificada (autotest 27/27 y 38 min de uso real sin cortes; `docs/INVESTIGACION-CORTES-6MIN.md`).
- Un códec comprimido añadiría latencia, CPU y una dependencia nueva sin resolver nada medido: ~1,5 Mbit/s caben de sobra en la Wi-Fi local. Ya se evaluó FLAC (`docs/ROADMAP.md`). MP3/AAC quedan como alternativa si un día faltara ancho de banda.

### Sonos: control local, no `loadStreamUrl`
`loadStreamUrl` es de la Control API en la nube (`api.ws.sonos.com`, OAuth, sesión de reproducción) y está pensada para radio por Internet, no para una URL privada del PC. Audiora usa el control local UPnP (AVTransport), que no necesita cuenta ni nube y es el mecanismo validado con el Sonos Five. Riesgo: UPnP no es una API pública de Sonos y podría restringirse.

### Red y firewall
El servidor escucha solo en la IP local del PC en la red del Sonos (no `localhost` ni `0.0.0.0`) y rechaza cualquier cliente que no sea un Sonos descubierto. No hay reenvío de puertos ni exposición a Internet. El firewall no se modifica (`docs/NETWORKING.md`).

## 2. Silenciar en el PC solo la app elegida

Antes, en «Una aplicación», «Silenciar el PC» silenciaba **el dispositivo** donde sonaba la app: todo lo demás de ese dispositivo (Chrome, juegos) se quedaba mudo. Se midieron las alternativas con CaptureCheck (tonos en una salida que nadie escucha; nada audible):

| Prueba (`CaptureCheck local-silence`) | Captura de la app (→ Sonos) | Salida local |
|---|---|---|
| Referencia | −12,0 dBFS | −12,0 dBFS |
| Sesión de la app silenciada (`ISimpleAudioVolume.Mute`) | **−163,1** | −240 |
| Volumen de la sesión al 0 % | **−163,1** | −240 |
| Volumen de la sesión al 10 % | −32,0 | −32,0 |

El silencio y el volumen **de la sesión** se aplican antes de la captura por proceso: silenciar la sesión también silenciaría el Sonos. **Descartado.** (El silencio y el volumen **del dispositivo**, en cambio, no afectan a la captura por proceso: medido con `CaptureCheck endpoint`.)

Alternativa adoptada, como [AppAudioRouter](https://github.com/Eververdants/AppAudioRouter) (MIT): **enviar solo esa app a una salida que nadie escucha** con la «salida por aplicación» de Windows (Configuración → Sonido → Mezclador de volumen), la misma que usan el mezclador y EarTrumpet (`IAudioPolicyConfigFactory`, no documentada; ranuras 25/26; IID `ab3d4648-…` desde la build 21390). Medido con una instancia propia de Chrome (perfil temporal, tono a −60 dBFS) que ya estaba sonando (`CaptureCheck route-browser`):

| | Captura de la app | Steam Streaming (nadie lo oye) | Salida predeterminada |
|---|---|---|---|
| Antes | −60,0 | — | −60,0 |
| Redirigida | **−60,0** | −60,0 | **—** |
| Restaurada | −60,0 | — | −60,0 |

Chrome cambia de salida **en caliente**; la captura no se interrumpe; nada más del PC cambia.

### Cómo se aplica (`AppOutputRedirect`, `StreamingSession.ApplyGuard`)
1. «Salida silenciosa»: un dispositivo virtual que no suena, hoy solo **Steam Streaming Speakers** (suena únicamente durante una sesión de Steam Remote Play). Nunca el predeterminado ni el de la app (`AudioDevices.FindSilentOutput`). VB-Audio CABLE y Voicemeeter se excluyen a propósito: a menudo sí se escuchan.
2. Windows solo acepta el cambio para un proceso con sesión de audio (si no, `E_INVALIDARG`), así que se usa el PID de la sesión, que en los navegadores es un proceso hijo (`AudioApps.SessionProcessOf`). Si la app aún no suena, se aplica en cuanto abre su audio (reintento cada 3 s; si entonces falla, plan B).
3. Antes de cambiar nada se anota la salida anterior en `%LOCALAPPDATA%\SonosFlow\app-output-guard.json`: **un registro por ejecutable**, escrito de forma atómica y protegido con un mutex con nombre. Si ya había un registro pendiente para esa app, se conserva su salida original (nunca se guarda la salida silenciosa como «anterior»). Cada registro lleva la instancia de Audiora que lo creó, y otra instancia (el autotest, una captura) no lo toca mientras esa siga abierta.
4. Al detener, cambiar de fuente o salir se restaura, solo si la salida sigue siendo la nuestra: si el usuario eligió otra mientras tanto, se respeta. Se prueba con cada proceso de la app, primero los que tienen audio. Si Windows aún no acepta ninguno (la app cerrada o sin audio), el registro queda pendiente y se reintenta al abrir Audiora y cada vez que se refresca la lista de apps, también mientras se transmite.
5. Ante cualquier fallo de la API (la factory no está documentada y puede cambiar), si el PC no tiene salida silenciosa o si Windows es anterior a 21H2, se aplica el plan B: silenciar la salida de la app, como antes. Queda anotado en el registro.

Nunca se silencia la salida predeterminada, nunca se cambia el dispositivo predeterminado del sistema ni el volumen de otras apps.

## 3. Brave, Chrome y otros navegadores

- Cada app se identifica por su **proceso raíz**: se sube por el árbol mientras el padre sea el mismo ejecutable (`AudioApps.RootOf`). Brave (`brave.exe`) y Chrome (`chrome.exe`) son raíces distintas y se capturan por separado; la captura incluye todos los procesos hijos (`INCLUDE_TARGET_PROCESS_TREE`), que es donde los navegadores generan el audio.
- Si la app se reinicia (PID nuevo), la sesión la vuelve a encontrar por la ruta del ejecutable (`FollowApplicationAsync`) y rehace la captura y la redirección.
- **Granularidad: por aplicación, no por pestaña.** Las pestañas de un navegador comparten un único proceso de audio; Windows no ofrece nada más fino. Para separar YouTube Music del resto, Audiora abre una instancia del navegador con un perfil propio (árbol de procesos propio).
- La salida por aplicación de Windows se guarda **por ejecutable**: al redirigir la instancia aislada de YouTube Music (que usa `brave.exe`) también se aparta el Brave normal mientras dura la transmisión, y lo mismo pasa con dos perfiles del mismo navegador o con las apps WebView2 (`msedgewebview2.exe`). Audiora lo anota en el registro cuando detecta otra instancia con audio. Con el plan B anterior (silenciar el dispositivo) esas instancias también se callaban, junto con todo lo demás.
- Si la app no tiene audio, la captura entrega silencio y la interfaz lo dice («Sin sonido» / «La aplicación no está abierta»); no se simula actividad.

## 4. Degradación «hacia los 6 minutos»

Investigada antes (`docs/INVESTIGACION-CORTES-6MIN.md`): la cadena del PC demostró no perder ni repetir muestras (prueba de resistencia a nivel de muestra); la causa estaba en la red o en el Sonos, que reproducía casi sin margen; se corrigió con el margen anti-cortes. Ahora se añade:
- `capture_gaps` en `stream-*.csv`: discontinuidades que Windows marca en la captura por proceso (`AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY`), antes ignoradas.
- MMCSS «Pro Audio» en el hilo de captura, como el ejemplo de Microsoft.
Métricas existentes: `dropped_chunks`, `backlog_ms`, `buffer_est_s`, `silence_kB`, `reconnects`, `peak_dbfs`, atascos de red y conexiones HTTP (`app-events.log`).

## 5. Riesgos y limitaciones

- `IAudioPolicyConfigFactory` no está documentada: su IID y su vtable han cambiado entre versiones de Windows. Solo se usa la variante de 21H2+, comprobada en este PC (build 26300); en otra build fallaría la activación y se aplicaría el plan B.
- Si Audiora se cierra de golpe, la app redirigida sigue apartada hasta que Audiora vuelva a abrirse y Windows acepte la restauración (la app abierta y con audio; si la app suena antes de eso, se restaura en el siguiente refresco, unos segundos). También se puede cambiar a mano en Configuración → Sonido → Mezclador de volumen.
- Apps que no siguen el cambio de salida en caliente (las que fijan un dispositivo concreto) seguirían sonando en local hasta reiniciar su audio. Chromium sí lo sigue (medido).
- `Application Loopback` está documentado desde la build 20348; Audiora lo usa desde la 19041 (2004), donde funciona según la comunidad.

## 6. Pruebas (28/10/2026)

**Automáticas** (`dotnet test`): 35 de Core (4 nuevas de la redirección), 86 de la app, 12 de copias: todas correctas.

**CaptureCheck** (tonos en salidas que nadie oye o a −60 dBFS; nada audible):

| Prueba | Resultado |
|---|---|
| Aislamiento (por defecto) | A −12,0 / B −91,9 dBFS: solo llega la app elegida |
| `local-silence` | silenciar la sesión silencia la captura (−163 dBFS) → descartado |
| `route-browser` (Chrome propio) | cambia de salida en caliente; captura sin cambios; predeterminada en silencio |
| `redirect-crash` (Chrome propio, el hijo muere sin restaurar) | sigue apartado tras el «cuelgue»; `RecoverPending` lo devuelve a su salida y borra el registro |
| `follow-check` (app WASAPI de dispositivo fijo) | NO sigue el cambio → Audiora lo detecta y aplica el plan B |
| `soak 30 --margin 1000` | 30 min, 0 cortes en «Una aplicación» y «Todo el PC»; 1802,3 s de audio en 1802,5 s; hueco máx. 11,8 ms; 0 descartes |

**Con el Sonos Five** (`--selftest-session`): **31/31**. La primera ejecución dio 30/31: con una app WASAPI de dispositivo fijo, Windows la redirigía pero seguía sonando en local (17c). De ahí la comprobación posterior a la redirección (`VerifyRedirect`) y el cambio de la parte B2 a una instancia de Chrome. Resultado de la parte B2: Chrome en la salida predeterminada (−61,1 dBFS) → apartado a Steam Streaming, el Sonos lo recibe (−59,7 dBFS), en el PC baja a −86,1 dBFS sin silenciar ninguna salida, y al detener vuelve a su salida. Después: ninguna salida silenciada, puerto libre, sin archivos de estado.

**Aceptación con las apps reales del usuario** (28/10, 17:11, `CaptureCheck observe`): música en Brave y otro contenido en Chrome; Audiora en «Una aplicación» → Brave con «Silenciar el PC». Brave pasa a sonar solo en Steam Streaming (detectado a los 3 s: «la app ya suena solo en la salida silenciosa») y el Sonos lo reproduce (PLAYING, búfer ≈ 1,6 s con margen de 1 s, 0 descartes, 0 reconexiones, `capture_gaps` 0). Chrome y el resto siguen en el BenQ (−16 dBFS RMS) y ninguna salida está silenciada. El usuario lo confirmó de oído: Brave solo en el Sonos y Chrome por los altavoces.

**Pendiente:** comprobar la restauración de Brave al detener esa transmisión y una transmisión real larga (30 min) con el Sonos.


## 7. Silenciar el PC en directo (29/10/2026, rama motor/silenciar-en-directo)

«Silenciar el PC» se puede activar y desactivar con la transmisión en marcha (StreamingSession.SetMuteLocalAsync, ReconcileMute): desactivar libera la app a la salida que Windows use en ese momento; activar la aparta de nuevo. La captura y el Sonos no se tocan. Los cambios rápidos se resuelven con el último deseado (_wantedMute) bajo _switchLock; una sesión que ya se desmonta (_tornDown) no se vuelve a proteger.

Autotest con el Sonos Five: **34/34** (17-pre silencio de fondo, 17e desactivar y 17f reactivar en directo con Chrome privado; el tono de 1 kHz se detecta frente a las frecuencias vecinas, no por el nivel total). Una primera ejecución con música de Brave sonando dio 2 falsos fallos (17c, 17f); por eso 17-pre exige silencio de fondo (< −75 dBFS) y 17c/17f exigen además 20 dB menos que el tono inicial.

### Cierre de sesión de Windows (29/10/2026, rama `fix/recuperar-redireccion`)

Incidente real: una app (Brave) quedó fijada a «Steam Streaming» porque Audiora terminó al cerrar sesión de Windows sin restaurar su salida (`OnSessionEnding` lanzaba la salida sin esperarla). Corrección: `StreamingSession.RestoreLocalNow` (síncrona, marca la sesión como desmontada primero, espera el cerrojo como máximo 1 s) y `MainViewModel.RestoreLocalForShutdown`; `OnSessionEnding` la ejecuta en un hilo del pool y espera hasta 5 s antes de la salida normal (el mutex del registro de redirecciones espera como máximo 3 s). Si no llega a tiempo, el registro queda pendiente y se recupera en el siguiente arranque o refresco. `CaptureCheck recover-pending <archivo>` ejecuta esa recuperación a mano.

Autotest con el Sonos Five: **35/35**, incluida la comprobación 17g (la salida de la app se restaura al instante con la transmisión en PLAYING y sin reconexiones). Ejecuciones anteriores con música o Chrome abiertos dieron fallos por interferencia ajena; 17-pre ya impide la parte B2 cuando hay audio de fondo, y la parte B2 se omite si Chrome y Edge están abiertos.

## 8. La app seleccionada se cierra y vuelve a abrirse (07/11/2026, rama `fix/recuperar-app-reiniciada`)

**Qué quedaba ligado al proceso anterior:** la captura por proceso (`ProcessLoopbackCapture`, un árbol de procesos fijo) y la redirección de «Silenciar el PC» (por ejecutable, pero aplicada a la sesión del proceso viejo). El seguimiento que ya existía las rehacía con `SwitchSourceAsync`, pero (a) restauraba la salida de la app y la volvía a apartar (la app nueva sonaba un instante en el PC y cambiaba de salida dos veces) y (b) dejaba la misma conexión con el Sonos. Prueba real del 06/11 (ronda A, margen 60 ms): tras reabrir Brave la captura volvió sin huecos (máx. 27 ms, 0 relleno, 0 ceros dentro del audio, 0 descartes) y aun así hubo cortes hasta detener e iniciar a mano; lo único que eso renueva además es la conexión con el Sonos.

**Señal:** el **proceso**, nunca el audio. Mientras el proceso vive (suena, en pausa, en silencio, con una interrupción breve) no se toca nada. `SourceAppFollower` (lógica pura, probada con un probe falso) en cada comprobación del monitor (cada 3 s):

| Situación | Qué hace |
|---|---|
| El proceso de la app vive | nada (ni siquiera mira otras apps) |
| Terminó | lo anota una vez («fuente cerrada…»); la transmisión sigue con silencio |
| Sigue cerrada | solo `Process.GetProcessesByName` (barato); sin capturas, sin reconexiones, sin reintentos |
| Hay un proceso del ejecutable pero sin sesión de audio | espera |
| Instancia nueva (otro PID) con sesión de audio | reasocia **una vez**: captura del PID nuevo, «Silenciar el PC» trasladado (`AppOutputRedirect.Retarget`, sin restaurar entre medias; el registro con la salida original se conserva) y conexión nueva con el Sonos (`AbortClients` + `SetAVTransportURI`/`Play`, como al detener e iniciar), sin cambiar de estado (ni «Reconectando…» ni sonidos) |
| Falla | reintenta a los 3, 6, 12, 24 s; tras 5 intentos deja esa instancia hasta que aparezca otra |

No cambia: el volumen del Sonos (no se envía ningún `SetVolume`), los ajustes, el margen, «Silenciar el PC» ni «Todo el PC» (el seguidor solo existe en «Una aplicación»; cerrar otra app no lo afecta). Registro (sin audio ni contenido): causa y PIDs, inicio del intento, resultado y milisegundos que tardó.

**Pendiente:** prueba real con Brave y el Sonos (no se puede simular sin el altavoz).
