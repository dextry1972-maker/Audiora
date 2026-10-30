# Fase 1 — estabilidad y recuperación inteligente

Fecha: 29–30/10/2026. Base: `a60570e`. Rama: `mejoras/fase1-estabilidad-recuperacion`.

**La fase no se considera aceptada hasta medir dos horas de transmisión y la recuperación ante fallos controlados.** Las pruebas automáticas y las simulaciones no sustituyen esa medición con el Sonos.

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

## Verificación

Base automática: **135/135** (Core 37, App 86, Backup 12).

Pruebas nuevas:

- Endpoint simulado: fallo de restauración y reintento, dispositivo ausente y regreso, registro antiguo, registro corrupto, propietario vivo, cambios legítimos del usuario y 50 ciclos de protección/restauración.
- Sonos simulado mediante HTTP: red perdida y regreso, reinicio (`TRANSITIONING`→`PLAYING`), agotamiento de ocho intentos, petición bloqueada cancelable, SOAP 701 y XML inválido seguido de otra consulta correcta. Cubren cliente y política; no constituyen una simulación completa de `StreamingSession`.
- Captura simulada: parada inesperada sin error, conservación de excepción original y parada solicitada.
- Registros: rotación con límite y escrituras concurrentes sin mezclar líneas.
- Informe: campos permitidos, ausencia de ruta de usuario y rechazo de sobrescritura.

Ejecuciones reales de la aplicación: exportación de diagnóstico con código 0; arranque, descubrimiento del Sonos y cierre mediante `--screenshot`, código 0; imagen revisada. El modo de captura no inicia una transmisión ni guarda ajustes.

Los resultados finales de regresión, CaptureCheck y revisión independiente se añaden tras completarlos.

## Límites y pruebas pendientes

- No hay medición nueva de dos horas ni evidencia de ausencia de cortes audibles en el Sonos por este informe inicial.
- La caída real de Wi-Fi, reinicio físico del Sonos, cambio del dispositivo predeterminado, desconexión física de un endpoint y cierre de sesión de Windows requieren pruebas controladas. No se cambia red, firewall ni servicios para simularlas.
- El seguimiento de otro PID ya existe; no se cambia su política. Un fallo de captura termina de forma segura, sin afirmar recuperación automática de cualquier error COM. No se implementa rediscovery por UDN ante cambio de IP DHCP.
- El guardia local conserva cambios de volumen distintos del valor aplicado; no puede distinguir que el usuario eligiera exactamente el mismo valor. La identificación del propietario mantiene la convención de nombre de proceso existente; el reciclado de PID sigue siendo un riesgo de detección de propietario.
- Registros heredados corruptos se conservan y bloquean una nueva protección: requieren diagnóstico; nunca se descartan sus originales para intentar continuar.
- No se ha modificado XAML, traducciones, formato PCM, latencia, margen, API de enrutamiento ni configuraciones del usuario. Tampoco se ha cambiado el volumen/EQ del Sonos.
