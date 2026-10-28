# Cambios

Formato: lo que cambia para quien usa Audiora, con el commit. Más reciente arriba.

## Sin publicar (rama `mejoras/hitos-arquitectura`)

### Añadido
- **Indicador de volumen en pantalla (HUD):** al usar los atajos aparece un momento «Bedroom · 45 %» (o «En pausa» / «Reanudado») abajo en el centro, sin tomar el foco. No se ve sobre juegos en pantalla completa exclusiva (limitación de Windows).
- **Temporizador de apagado:** debajo del botón de transmitir, detener en 30, 60 o 90 min, con cuenta atrás; se cancela al elegir «Sin temporizador» o al detener.
- Documentación de arquitectura, flujo de audio, red, pruebas y hoja de ruta (`docs/`), `AGENTS.md` y definiciones de agentes en `.claude/agents/`.

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
