# Hoja de ruta

Estado a 28/10/2026, rama `mejoras/hitos-arquitectura` (sin fusionar en `main`, sin instalar). Leyenda: ✅ hecho y verificado · 🟡 hecho, verificación parcial · ⏳ pendiente · ⛔ bloqueado.
«Verificado» indica cómo: **auto** (pruebas automáticas), **sim** (tubería local / CaptureCheck), **Sonos** (altavoz real).

## Hitos

| # | Hito | Dueño | Estado | Criterio de aceptación | Verificación |
|---|---|---|---|---|---|
| 1 | Fiabilidad del audio (cortes ~6 min) | audio + red | 🟡 | Sin cortes audibles en uso normal; recuperación acotada; el silencio no es fallo; no pelear con el usuario | auto (42/42); sim (soak 8 + 6 min, 0 cortes); **Sonos (autotest 26/26, incl. pausa/Stop desde el Sonos)**; revisor: aprobado con reservas (resueltas); falta uso real prolongado y un corte de Wi-Fi real |
| 2 | Título y artista en el mini reproductor | windows + ui | 🟡 | Muestra la pista de la app capturada; nada erróneo si no coincide | reglas de coincidencia probadas con sesiones reales y de prueba; capturas; falta verlo con YouTube Music sonando de verdad |
| 3 | HUD de volumen | ui | 🟡 | Aparece al usar los atajos sin tomar el foco | captura `--screenshot-hud`; falta pulsarlo con el Sonos sonando |
| 4 | Arranque fiable | red + windows | 🟡 | Espera acotada a la zona Wi-Fi y al Sonos; sin dobles inicios | auto/revisión de la lógica; detener durante «Conectando…» revisado; falta prueba real de inicio con Windows |
| 5 | Temporizador de apagado | ui | 🟡 | 30/60/90 min, cancelable | compila y se ve en la interfaz; falta prueba de expiración real |
| 6 | Evaluación de formato (FLAC) | audio | ✅ decisión | Decidir con datos | ver «Evaluación de formato» |

### Hito 1 — detalle
- ✅ Margen anti-cortes efectivo (`8c39887`): búfer del Sonos 1,1–1,4 s medido (antes 0,1–0,2 s).
- ✅ Telemetría: `backlog_ms`, conexiones, atascos (`StallTracker`), indicador «Red».
- ✅ `PlaybackWatchdog` + `RecoveryPolicy`: detecta Sonos que no consume o con posición congelada; reintentos 5→60 s, máx. 8 / 10 min; no reemite si el Sonos tiene otra fuente o se pausó desde la app de Sonos.
- ✅ Monitor a prueba de excepciones; limpieza garantizada; sondeos de 2 s.
- ⏳ Productor muerto (0 bytes y cola vacía 3 s → recrear captura) y cliente sobrante (>1 conexión, una parada): propuestos por el ingeniero de audio, no implementados (no observados nunca).
- ⏳ Redescubrir por UDN si el DHCP de la zona Wi-Fi cambia la IP del Sonos durante una sesión (hoy: tras agotar reintentos la sesión falla con mensaje).
- ⏳ Sondeo unicast a la última IP conocida del Sonos al arrancar (más rápido que SSDP).

## Evaluación de formato (hito 6)

Pregunta: ¿cambiar WAV PCM 16 bit/48 kHz (1,536 Mbit/s) por FLAC u otro formato?

| Criterio | Medido | Fuente |
|---|---|---|
| Ancho de banda WAV | 192–194,5 kB/s de media | `stream-*.csv`, 50 min reales (27/10) |
| Atascos de red con margen 1 s | **0** segundos con cola > 300 ms; 0 con < 115 kB/s; cola máx. 40 ms | mismas sesiones |
| CPU del pipeline WAV completo | **0,16 % de un núcleo** (captura + servidor + lector + detector) | soak, 20 s de muestreo |
| Latencia añadida por WAV | 0 (tramas de 10 ms, sin codificador) | código |
| Compresión FLAC esperada | ~50–60 % del tamaño (música típica) | **bibliografía, no medido**: no hay codificador FLAC en el PC y no se instaló ninguno |
| Latencia añadida por FLAC | ≥ 1 bloque (4096 muestras ≈ 85 ms) + reprogramar el envío por tramas | estimación |
| Soporte del Sonos a FLAC en directo infinito | **no verificado** | — |

**Decisión: mantener WAV.** Con el margen de 1 s no hay ningún atasco medido, la CPU es despreciable y el ancho de banda (1,5 Mbit/s) está muy por debajo de la Wi-Fi. FLAC añadiría un codificador (dependencia nueva o implementación propia), latencia y un riesgo sin verificar con el Sonos, para resolver un problema que hoy no aparece en los datos.

**Reabrir si:** el indicador «Red» muestra atascos repetidos con margen ≥ 1 s, o se añaden varios Sonos (el ancho de banda se multiplica por altavoz). Prueba entonces: prototipo FLAC (nivel 0, bloque 1152) detrás de un ajuste oculto, soak de 30 min y autotest con el Sonos comparando `backlog_ms`, atascos y búfer.

## Riesgos abiertos
- Ahorro de energía de la zona Wi-Fi de Windows: la apaga si no hay clientes; es configuración de red del usuario (no se toca).
- Comportamiento de `RelTime` y `PAUSED_PLAYBACK` en todos los firmwares: verificado solo en el Sonos Five del usuario (Sonos 96.1).
- La cabecera WAV declara ~6,2 h; el Sonos sigue reproduciendo más allá (sesión de 43 min sin problema; no probado > 6 h).
- GSMTC: formato del AUMID de Chromium no es un contrato y puede cambiar.
- Detectados en la revisión de documentación, sin corregir (bajo impacto):
  - Al cambiar de fuente con «Silenciar el PC» se restaura la salida anterior antes de silenciar la nueva: si es la misma salida, puede oírse un instante (no comprobado).
  - Salidas 5.1/7.1: solo se envían los canales delantero izquierdo y derecho (`LoopbackSource`), sin mezclar centro ni envolventes.
  - Con más de una conexión del Sonos a la vez, `buffer_est_s` suma los bytes de todas.
  - Las conexiones rechazadas por la lista blanca se cuentan pero no se registran.
  - Los modos de prueba comparten `output-guard.json` y registros con la copia del usuario: no ejecutar `--selftest-session` con Audiora abierto (ya es norma en `docs/TESTING.md`).
  - El instalador borra la carpeta de destino entera si contiene `SonosFlow.exe`: seguro con la ruta por defecto, peligroso con un `/dir` compartido.

## Siguientes pasos recomendados
1. Usar Audiora varios días y revisar «Red» y `app-events.log` (líneas «vigilante», «reintento», «atasco»).
2. Probar el inicio con Windows real con «Reanudar al abrir» activo.
3. Implementar los dos detectores pendientes del hito 1 si aparecen en los registros.


## Fase 1 — estabilidad (30/10/2026)

Rama `mejoras/fase1-estabilidad-recuperacion`; integración/push no autorizados ni realizados. Informe: [FASE1-ESTABILIDAD.md](FASE1-ESTABILIDAD.md).

- ✅ **Auto:** protección de registros de restauración, mutex/propietarios, conservación de cambios del usuario, contabilización de saturación y detección de captura terminada; regresión 164/164, build sin avisos.
- ✅ **Revisión independiente:** sin P1/P2 abiertos en las correcciones; cerrada la carrera de `Dispose` tardío y reforzado el detector del ensayo prolongado.
- ✅ **Sim/captura Windows:** aislamiento y prueba de endpoint superados; salida virtual restaurada.
- 🟡 **Sim:** pruebas de resistencia 9 min / control de corte / 7 min con margen, en ejecución.
- 🟡 **Sonos:** ensayo real de dos horas iniciado a las 00:17; pendiente medir y revisar el resultado y la restauración final.
- ⏳ **Manual:** corte físico de Wi-Fi, dispositivo predeterminado cambiado, desconexión física del endpoint y cierre de sesión de Windows. No se declara aceptación completa por cubrir únicamente simulaciones.
