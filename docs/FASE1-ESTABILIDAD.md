# Fase 1 — estabilidad y recuperación inteligente

Fecha: 29–30/10/2026. Base: `a60570e`. Rama: `mejoras/fase1-estabilidad-recuperacion`.

**El usuario redujo el ensayo inicial a diez minutos y después pidió terminar para descansar. Se detuvieron las pruebas. Tanto completar los diez minutos como la validación prolongada de dos horas quedan pendientes.** No se declara cerrada la fase.

## Punto de recuperación

Copia previa creada y verificada por BackupCli (SHA-256, 771 archivos):
`C:\Users\Admin\SonosFlow-Backups\SonosFlow_2026-10-29_23-52-04`.
Git inicial: `main`, únicamente `.codex/` sin seguimiento. Esa carpeta se conserva.
No se instala, fusiona ni publica esta compilación.

Para revertir, revisar primero `sfbackup plan SonosFlow_2026-10-29_23-52-04`; una restauración requiere confirmación y crea otra copia antes de sobrescribir. No ejecutar `git reset --hard` ni limpiar archivos ajenos.

## Investigación y problemas reales

- Ya existían `PlaybackWatchdog`, ocho reintentos con retroceso, cancelación del inicio, seguimiento de procesos reiniciados, restauración de redirecciones y restauración síncrona al cerrar Windows. Se conservan.
- `LocalOutputGuard.RecoverPending` borraba el registro en `finally`, incluso cuando Windows no encontraba el dispositivo o rechazaba la restauración. `Dispose` marcaba la operación terminada antes de restaurar. Un fallo temporal podía perder la única información para recuperar el sonido.
- El registro del silencio local se escribía directamente, carecía de propietario y podía sobrescribirse desde una segunda instancia de prueba. La restauración de nivel completo también sobrescribía cambios de volumen del usuario.
- `AppOutputRedirect.StateLock` ignoraba el resultado de `WaitOne(3 s)` y permitía continuar sin exclusión. Un registro ilegible se interpretaba como vacío y un guardia vivo del mismo ejecutable podía sustituirse.
- El servidor usaba `DropWrite` pero interpretaba `TryWrite == true` como audio conservado. En un canal saturado devuelve `true` aunque descarta el nuevo bloque: los descartes quedaban ocultos y el pendiente incluía bytes inexistentes. Reproducido mediante canal acotado y prueba TCP.
- `RecordingStopped` sin excepción no marcaba fallo. El relleno de silencio podía mantener una transmisión aparentemente activa después de terminar la captura.
- `app-events.log` y `errors.log` crecían sin límite. Los CSV no incluían memoria y CPU del proceso.

Los registros recientes contienen restauraciones correctas de Brave y un episodio de alternativa por silencio de dispositivo; no demuestran por sí solos los nuevos fallos inyectados. Los errores antiguos de claves duplicadas en la lista de aplicaciones ya están corregidos mediante agrupación en la base: no se vuelve a modificar esa lógica.

## Correcciones

- Registro local atómico, vaciado a disco antes de silenciar, exclusión mediante mutex y propietario compatible con registros anteriores. Un registro pendiente bloquea otro guardia hasta conocer su estado.
- Restauración repetible: se elimina el registro solo tras éxito; dispositivo ausente o JSON ilegible conservan la evidencia. Se respeta un nivel que el usuario cambió. El sondeo existente reintenta recuperar un dispositivo que vuelve a estar disponible, sin tocar guardias vivos. La recuperación de inicio corre fuera del dispatcher.
- Redirección por aplicación: exclusión efectiva y conservación del registro pendiente; la API de enrutamiento de Windows se mantiene.
- Cola HTTP acotada con rechazo observable, sin bloquear el hilo de audio ni cambiar la capacidad, el formato o el margen.
- Detección de parada inesperada de captura, incluso sin excepción; una parada normal no se presenta como fallo. Se utiliza la salida segura existente de la sesión: no se añaden reinicios de captura sin verificar.
- Rotación de registros de eventos y errores: 2 MiB por archivo, tres archivos anteriores, exclusión entre procesos. El fallo de diagnóstico no debe derribar la sesión. Los CSV siguen siendo uno por sesión; no se borran automáticamente.
- CSV: se añaden al final `private_memory_mb`, `working_set_mb` y `process_cpu_pct`. La CPU es el porcentaje del equipo completo durante el intervalo real, normalizado por procesadores lógicos; incluye toda Audiora, no solo el motor.
- Exportación opcional: `SonosFlow.exe --diagnostic-report <archivo-nuevo.json>`. Sale sin abrir interfaz, capturar, recuperar dispositivos, guardar ajustes o controlar el Sonos. Incluye versiones, contadores del proceso exportador y presencia de restauraciones pendientes. No incluye audio, credenciales, rutas personales, nombres de aplicaciones/dispositivos, IP ni registros sin filtrar. Rechaza sobrescribir un archivo existente. Es una instantánea del exportador, no una medición de una sesión activa.

## Resultados al detener el trabajo

- Regresión automática: **164/164** (65 Core, 87 App, 12 Backup); 29 casos nuevos respecto a 135 iniciales. Compilación Release no incremental: 0 errores y 0 avisos.
- Estrés y recuperación: cinco repeticiones de 35 casos, **175/175 ejecuciones correctas**. Incluyen saturación y drenaje TCP, cancelación, Sonos simulado inaccesible con ocho intentos acotados, retorno de red, mutex ocupado, registros corruptos y recuperación del audio. Los 50 ciclos de guardia por repetición suman 250 ciclos simulados.
- Captura Windows: aislamiento y prueba de endpoint correctos; silencio y volumen virtual restaurados.
- Simulación local: 9 min sin discontinuidades ni descartes; el control positivo detectó el corte inyectado (código 1 esperado); repetición aislada de 7 min con margen 1000 ms correcta, 0 discontinuidades y 0 descartes en ambas capturas.
- Una primera prueba de margen falló al solaparse con el autotest físico que silenciaba el mismo endpoint. Se conserva como ensayo contaminado; no se oculta ni se considera aprobado. La repetición aislada es la válida.
- El estrés detectó una suposición frágil del test de backlog, dependiente del búfer TCP. Se aumentó la carga y el drenaje a 20 s, conservando la exigencia de crecimiento y recuperación de la cola; las cinco repeticiones posteriores pasan.
- Aplicación real: exportación de diagnóstico y captura de pantalla completadas; imagen revisada. El diseño y los idiomas no se modifican.

### Sonos real: medición parcial, no aceptación

El primer intento de dos horas fue interrumpido a los 450 s, precedido por órdenes registradas como «usuario (botón o bandeja)». El autotest continuó y terminó con cinco fallos. Hubo cambios de fuente y audio de fondo. El volumen Sonos pasó de 50 a 39 entre instantáneas; EQ igual. No se atribuye ese cambio al código ni se revierte una posible elección legítima.

Se añadió un modo independiente `--selftest-session PID --selftest-out archivo --selftest-soak-only --selftest-soak-seconds 600 --minimized`: captura solo el tono de aplicación, sin silenciar el PC ni transmitir otras aplicaciones; omite las partes A/B/C. La cancelación queda registrada aunque haya Stop→Start entre sondeos. Revisión independiente aprobada por lectura.

El ensayo inicial de diez minutos se detuvo anticipadamente por petición final del usuario. CSV `stream-20261030-003637.csv`: 281 filas y 285 s de cobertura, 0 filas anómalas, 0 reconexiones, 0 descartes y 0 discontinuidades de captura. Memoria privada mediana 72.4 MiB (rango 66.2–77.6); CPU media 0.21 % del equipo. Es una medición corta, no evidencia de estabilidad de diez minutos ni de dos horas, ni medición acústica.

Al cerrar se comprobó: todas las salidas sin silenciar, ausencia de registros de restauración pendientes y puerto 8780 libre. Solo se detuvieron los procesos propios de la prueba. No se restauran volúmenes que el usuario pudiera haber cambiado.

## Pendientes y riesgos

- **Completar diez minutos y validar dos horas: pendientes.** No se declara terminada la fase.
- Repetir el autotest físico completo en condiciones controladas: su ejecución anterior de cinco fallos no se considera aprobada.
- Corte real de Wi-Fi, reinicio físico del Sonos, cambio de dispositivo predeterminado durante reproducción, desconexión física del endpoint y cierre de sesión Windows requieren pruebas manuales controladas.
- Los tests HTTP cubren cliente y política, no toda StreamingSession ni SSDP. Ante fallo de captura se conserva la salida segura; no se implementa recreación automática no verificada ni rediscovery por UDN tras cambio DHCP.
- CSV por sesión sin purga automática; registros corruptos conservados. El reciclado de PID sigue siendo una limitación de identificación del propietario.
- No hay una medición previa comparable de CPU/memoria para atribuir mejoras. Las métricas parciales no demuestran ausencia de fugas a largo plazo.
- El código de salida de la aplicación puede ser 0 aunque falle el autotest: revisar RESULTADO y CSV.

## Archivos modificados

- Documentación: AUDIO_PIPELINE.md, ROADMAP.md, TESTING.md y este informe.
- App: App.xaml.cs; Infrastructure/AppLog.cs, DiagnosticReport.cs y SelfTest.cs; ViewModels/MainViewModel.cs y MainViewModel.Sources.cs.
- Core: Audio/AppOutputRedirect.cs, LocalOutputGuard.cs y LoopbackSource.cs; Net/WavStreamServer.cs; Sonos/SonosClient.cs; Streaming/StreamingSession.cs; Diagnostics/RotatingLog.cs; TestVisibility.cs.
- Tests: App/DiagnosticReportTests.cs; Core/AppOutputRedirectTests.cs, WavStreamServerTests.cs, LocalOutputGuardTests.cs, LoopbackSourceTests.cs, RotatingLogTests.cs y SonosClientTests.cs.

## Revisión y estado de Git

Revisión independiente sin P1/P2 abiertos en las correcciones revisadas. QA conserva los ensayos fallidos y distingue simulación, captura Windows y Sonos real.

Rama local `mejoras/fase1-estabilidad-recuperacion`, primer commit de implementación `763ed99`, seguido de un commit de ajuste y resultados. Sin merge ni push. `.codex/` preexistente se conserva sin seguimiento. No se modifica XAML, traducciones, formato PCM, latencia, margen ni API de enrutamiento.

Evidencias locales: `C:\Users\Admin\.codex\visualizations\2026\10\30\01a18aee-385f-7ea3-affa-448b88ff7b1f`. Copia verificada previa: `C:\Users\Admin\SonosFlow-Backups\SonosFlow_2026-10-29_23-52-04`.
