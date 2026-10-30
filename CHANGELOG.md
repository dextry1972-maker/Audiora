# Cambios

Formato: lo que cambia para quien usa Audiora, con el commit. Más reciente arriba.

## Sin publicar (rama `feature/hotspot-sin-dispositivos`)

### Aviso para activar el hotspot cuando no hay altavoces
- Si una búsqueda termina sin altavoces, Audiora mira el estado del hotspot de Windows (solo lectura) y avisa **una vez** por episodio sin altavoces:
  - **Apagado:** «¿Quieres activar el hotspot para conectar otros equipos?» con «Activar hotspot» / «Ahora no». Solo se enciende si pulsas el botón.
  - **Ya encendido:** indica que los otros equipos deben conectarse a esa red y ofrece «Buscar de nuevo».
  - **Desconocido** (sin conexión compartible, política de Windows, error de la API): lo dice sin suponer que está apagado y ofrece «Abrir configuración».
- No avisa mientras se transmite ni cuando el hotspot está arrancando por sí solo (p. ej. al iniciar sesión). Sin administrador ni registro.
- Textos en los 12 idiomas. Probado a mano con Windows real (hotspot apagado → «Activar hotspot») el 30/10/2026. Detalles: `docs/NETWORKING.md` §11.

### Rediseño «liquid glass» (ya en main, `a60570e`)
- Barra lateral con Inicio y Ajustes, fondo aurora, ondas de puntos alrededor del botón, una tarjeta de cristal en Inicio, cabeceras de Ajustes con icono; mini reproductor, bienvenida y HUD con el mismo cristal. Todos los controles y funciones se conservan.
- Las animaciones van a 20 fps y se desactivan si Windows reduce las animaciones (CPU en reposo ≈ 1 %).
- Límite: WPF no tiene desenfoque real del fondo; el cristal se simula con aurora, paneles translúcidos y bordes iluminados.

## Fusionado en main (rama `motor/audio-selectivo`)

### Silenciar en el PC solo la app que transmites
- En «Una aplicación», **«Silenciar el PC» ya no silencia el dispositivo**: Windows envía solo esa app a una salida que nadie oye (Steam Streaming Speakers). El Sonos la sigue recibiendo, y Chrome, los juegos y las demás apps siguen sonando en el PC. Al detener, cambiar de fuente o salir vuelve a su salida, y también tras un cierre inesperado.
- Si la app no cambia de salida en caliente, si el PC no tiene esa salida o si Windows es anterior a 21H2, se silencia su salida como antes; queda anotado en el registro.
- Diagnóstico: el hilo de captura usa prioridad de audio de Windows (MMCSS) y `stream-*.csv` añade `capture_gaps` (huecos que Windows marca en la captura).
- Detalles y mediciones: `docs/MOTOR-AUDIO-SELECTIVO.md`.

## Fusionado en main (rama `i18n/sistema-multilingue`)

### Idiomas
- **12 idiomas:** español, inglés, portugués (Brasil), francés, alemán, italiano, ruso, chino simplificado, japonés, coreano, hindi y árabe (de derecha a izquierda). Todas las pantallas, tooltips, nombres de accesibilidad, mensajes de error, la bandeja y el instalador. Las traducciones que no son del español original las hizo IA y necesitan revisión nativa (`docs/LOCALIZATION.md`).
- **Bienvenida de idioma** solo en una instalación nueva: búsqueda, nombres nativos y el idioma de Windows sugerido. Quien actualiza desde una versión anterior sigue en español, sin bienvenida, y se conservan sus ajustes.
- **Ajustes → Idioma:** cambia al instante sin cortar la transmisión ni tocar el altavoz, la fuente o el volumen.
- El instalador aparece en el idioma de Windows.
- Los registros (`app-events.log`, `stream-*.csv`) no cambian con el idioma.

## Sin publicar (rama `mejoras/hitos-arquitectura`)

### Fiabilidad
- **Vigilante de reproducción:** detecta un Sonos que dice «reproduciendo» pero no recoge audio o tiene la posición congelada, y reconecta solo. El silencio nunca cuenta como fallo.
- **Reintentos acotados:** 5→60 s de espera, máximo 8 (antes: cada 5 s sin fin). Un fallo momentáneo del canal de control ya no corta el audio.
- **No pelea con el usuario:** si el Sonos pasa a otra fuente, se pausa o se detiene (dos veces) desde la app de Sonos, Audiora se retira sin enviar Stop. Verificado con el Sonos Five.
- **Arranque:** al reanudar al abrir espera hasta 5 min a la zona Wi-Fi y al Sonos.
- Cambio de fuente sin hueco; la limpieza siempre restaura el PC y libera el puerto; detener o salir durante «Conectando…» ya no deja nada colgado.

### Añadido
- **Título y artista** de lo que suena (información de reproducción de Windows) en el mini reproductor y en «Fuente de audio».
- **Indicador de volumen en pantalla (HUD):** al usar los atajos aparece un momento «Bedroom · 45 %» (o «En pausa» / «Reanudado») abajo en el centro, sin tomar el foco. No se ve sobre juegos en pantalla completa exclusiva (limitación de Windows).
- **Temporizador de apagado:** debajo del botón de transmitir, detener en 30, 60 o 90 min, con cuenta atrás; se cancela al elegir «Sin temporizador» o al detener.
- Documentación de arquitectura, flujo de audio, red, pruebas y hoja de ruta (`docs/`), `AGENTS.md` y definiciones de agentes en `.claude/agents/`.
- Evaluación de FLAC: se mantiene WAV (0 atascos medidos, 0,16 % de CPU); ver `docs/ROADMAP.md`.
- El instalador crece ~6 MB (proyección de Windows para el título y artista).

## 2026-10-27

- `80ce9f1` **Atajos de teclado globales** (Ctrl+Alt+↑/↓ volumen, Ctrl+Alt+P pausa, Ctrl+Alt+O mini reproductor) e **indicador «Red»** de atascos de la Wi-Fi, anotados con su hora en los registros. README al día.
- `8c39887` **Margen anti-cortes efectivo:** el Sonos recibe el margen completo aunque se conecte antes de que se haya capturado (relleno con silencio inicial). Validado con el Sonos Five: búfer estable 1,1–1,4 s (antes 0,1–0,2 s); 38 min de uso real sin cortes.
- `3a911f6` Margen anti-cortes configurable (0/1/2/3 s, 1 s por defecto) y telemetría de red (`backlog_ms`, conexiones del Sonos) para investigar los cortes hacia los 6 min.

## 2026-10-26

- `bbfc0cd` «Silenciar el PC» también en modo «Una aplicación», sin tocar el volumen maestro de Windows.
- `c3846e0` Mini reproductor flotante y captura de audio por aplicación (incluido YouTube Music aislado).
- `9479dd5` Accesos directos y título del stream con el nombre Audiora.
- `126bc84` Nombre visible «Audiora» (identificadores técnicos sin cambios).
- `0f2c383` Sistema de copias de seguridad y restauración del proyecto (`sfbackup`).
- `0f76514` Versión inicial: captura WASAPI → WAV por HTTP → Sonos (UPnP), silenciar el PC, instalador por usuario.
