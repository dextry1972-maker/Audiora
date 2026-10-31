# Audiora — Sonidos «Enchanted»

Sistema de sonidos de interfaz: originales (síntesis, sin grabaciones ni melodías ajenas), locales y **aislados del motor de transmisión**. Voz principal: **marimba suave** con acentos de campanilla de cristal (elegida tras probar ocarina, cristal, arpa, kalimba y marimba; el resto siguen en `EnchantedSynth` como alternativas: `DefaultTimbre`). Código en `src/SonosFlow.App/Sound/` y `ViewModels/MainViewModel.Sounds.cs`. `src/SonosFlow.Core` no cambia.

## 1. Lo que condicionó el diseño (auditoría)

- **Captura «Todo el PC»** = loopback del dispositivo de salida: graba *todo* lo que suena en él. Un sonido de Audiora en ese dispositivo **llegaría al Sonos** (comprobado: pico −13,3 dBFS en la captura del dispositivo).
- **Captura «Una aplicación»** = loopback por proceso (`ProcessLoopbackCapture`): solo ese árbol de procesos; los sonidos de Audiora **no entran** (comprobado: −59,7 dBFS = solo el tono de prueba).
- «Silenciar el PC» silencia el dispositivo (o redirige la app a «Steam Streaming Speakers»): en «Todo el PC» el PC ya está mudo.
- Por eso el aislamiento es una **regla**, no una opción: `SoundGate.WouldReachSonos`. Con transmisión activa en «Todo el PC» y el dispositivo de efectos = dispositivo capturado (o desconocido) **no suena nada**, ni las vistas previas. La comprobación se repite **justo antes de que arranque cada sonido**, con el dispositivo que realmente va a sonar (si el elegido está desenchufado suena el predeterminado) y el predeterminado consultado en ese instante; y al empezar a transmitir se cortan los sonidos que aún suenan en un dispositivo que se va a capturar. Con «Una aplicación», o con otro dispositivo de efectos, suena todo.
- No se tocó WASAPI de captura, búferes, redirección ni reconexiones: los efectos son otro cliente de render en el mismo proceso.

## 2. Arquitectura

| Pieza | Responsabilidad |
|---|---|
| `SoundCatalog` | 10 sonidos: categoría (qué interruptor lo gobierna), prioridad, intervalo mínimo, nivel relativo |
| `EnchantedSynth` | Síntesis determinista (marimba suave + campanilla de cristal + reverb corta), ADSR con fundidos, normalizado de nivel, sin clics; 5 timbres disponibles, marimba por defecto |
| `SoundPolicy` | Decide: interruptores, volumen 0, conflicto de captura, limitación por sonido y por grupo, tope de 3 voces |
| `SoundGate` | ¿Un sonido en este dispositivo llegaría al Sonos ahora? |
| `WasapiEffectOutput` | Un cliente WASAPI compartido + mezclador con limitador suave (nunca llega a plena escala); se abre al primer sonido, se cierra a los 3 s de reposo, se reabre si cambia el dispositivo, se aparca 10 s si falla (un aviso en el registro por caída); libera el dispositivo COM al cerrar |
| `SoundService` | Punto de entrada: decide en el hilo de quien llama y reproduce en el pool (la interfaz nunca espera); precarga (≈ 51 ms, 0,9 MB) |
| `SoundDirector` | Traduce **estados reales** del stream en sonidos |
| `SoundUiHooks` | Interruptores (clic) y desplegables de Ajustes (selección del usuario) |

Eventos → solo tras el hecho: conexión tras `Starting → Streaming` real; una sola pérdida por corte (`Streaming → Recovering`); reconexión solo tras recuperar; error distinto de desconexión; el arranque automático (al iniciar sesión) y el cierre de la app son silenciosos; confirmación de interruptor solo si el valor enlazado se mantiene.

## 3. Inventario

| Sonido | Evento | Interruptor | Duración | Idea |
|---|---|---|---|---|
| Connected | Conexión con el Sonos | Conexión | 900 ms | arpegio ascendente A–D–F♯–A, corona de campanilla |
| ConnectionLost | Pérdida de señal (una por corte) | Pérdida de conexión | 800 ms | dos notas descendentes, tono más oscuro, resonancia |
| Reconnected | Conexión restablecida de verdad | Conexión | 500 ms | par brillante ascendente |
| Error | Fallo al iniciar / sesión fallida | Error | 500 ms | semitono suave + nota grave: inconfundible con la pérdida |
| StreamStart | Iniciar transmisión | Notificaciones | 800 ms | quintas abiertas ascendentes |
| StreamStop | Detener transmisión | Notificaciones | 850 ms | las mismas quintas, descendentes, cola más larga |
| ConfigChanged | Cambio de configuración (desplegables de Ajustes) | Notificaciones | 170 ms | una nota de cristal |
| Navigate | Cambio de pestaña | Interfaz | 70 ms | tic casi inaudible (nivel bajo) |
| ToggleOn / ToggleOff | Interruptores | Interfaz | 160 ms | D→A / A→D |

Límites de frecuencia: por sonido (90–5000 ms), 110 ms entre microsonidos, 350 ms de silencio de microsonidos tras uno importante, máximo 3 voces (errores y eventos de conexión siempre pasan).

## 4. Ajustes → Sonidos

Cinco interruptores (Sonidos de interfaz, Notificaciones sonoras, Sonidos de conexión, Avisos de pérdida de conexión, Sonidos de error), **volumen independiente** 0–100 % (curva perceptual; 0 = silencio; al soltar suena una muestra), **dispositivo** (predeterminado o uno concreto; si se desconecta se usa el predeterminado y se indica), **vista previa** de cada sonido y **Restablecer**. Todo persiste en `settings.json` (instalación nueva: todo activado, 60 %, dispositivo predeterminado; archivos antiguos sin estas claves cargan con esos valores). Textos en los 12 idiomas.

No hay un interruptor «todo apagado»: volumen 0 silencia todo.

## 5. Pruebas

Automáticas (`tests/SonosFlow.App.Tests/SoundSystemTests.cs`): calidad de cada sonido (finito, pico ≤ 0,6, empieza y acaba en silencio, sin saltos de clic, sin DC, nivel homogéneo), duraciones, determinismo, distinción entre sonidos parecidos, política, aislamiento, director (secuencias de estados), servicio (no bloquea, un evento = un sonido, volumen, conflicto de captura, apagado), persistencia, WASAPI real **en silencio** (apertura, mezcla, cierre en reposo, dispositivo inexistente, handles) y **captura real de aislamiento** (necesita «Steam Streaming Speakers» y `CaptureCheck` compilado; si no, termina sin comprobar).

Manual corto (≈ 3 min), en una instalación con la rama:
1. Ajustes → Sonidos → pulsa cada botón de vista previa: deben sonar suaves y distintos.
2. Cambia pestañas y mueve interruptores: tics discretos; muévelos muy rápido: no se acumulan.
3. Una aplicación → Transmitir: suena Inicio y, al conectar, Conexión. Detener: suena Detención.
4. Todo el PC → Transmitir con el dispositivo de efectos «Predeterminado»: **silencio** (es lo correcto) y las vistas previas muestran el aviso. Elige otro dispositivo de efectos: vuelve a sonar y el Sonos no lo recibe.
5. Con el Sonos: apaga su Wi-Fi unos segundos: una sola «pérdida»; al volver, una sola «reconexión».

## 6. Riesgos y límites

- **No verificado con oído humano ni con el Sonos físico** en la implementación inicial: la calidad musical (qué tan «mágico» suena) la juzga quien escucha; la síntesis se comprobó con medidas, no con el oído.
- «Todo el PC» + dispositivo de efectos = dispositivo capturado → sin sonidos durante la transmisión (por diseño). Para oírlos, elegir otro dispositivo.
- La comprobación previa usa el listado de salidas con 1 s de caché (en el hilo de la interfaz); la comprobación final en el momento de sonar consulta el predeterminado sin caché.
- Revisión independiente (audiora-code-reviewer): sin fallos graves; se corrigieron los 9 hallazgos aplicables (doble aviso parada+error, huecos del aislamiento, fugas COM, limitador, estado inicial del director, navegación solo por clic, guardado del volumen al salir). Sin corregir por ser mínimos: `AudioDevices.Resolve` (en Core, no se toca) crea un enumerador COM por consulta, y las consultas COM de la lista de dispositivos se hacen en el hilo de la interfaz.
- En «Todo el PC» con el dispositivo predeterminado (el uso por defecto) solo suenan Detención y Error durante/tras la transmisión; Inicio, Conexión, Pérdida y Reconexión quedan mudos por el aislamiento. Para oírlos: «Una aplicación» u otro dispositivo de efectos.
- Windows puede aplicar su mezclador/volumen por aplicación a los efectos: son una sesión de audio de Audiora más.
- Integración con el trabajo paralelo de Codex en la rama `mejoras/movimiento-interfaz` (ajuste `InterfaceSounds`, `UiFeedback.cs`, claves `settings.general.sounds.*`): `AppSettings.InterfaceSounds` se llama igual para que el merge sea limpio, pero el `UiFeedback` de ese trabajo y este sistema se solapan y hay que unificarlos al fusionar.
