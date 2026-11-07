# Audiora

Audiora streams audio from your PC or a selected app to a compatible Sonos device.  Download and extract the ZIP, then open Audiora.exe. No separate .NET installation is needed.  This unsigned beta may trigger a Windows security warning. Please report bugs in the repository’s Issues tab.

---

*(Antes «Sonos Flow». Los nombres técnicos — proyecto `SonosFlow.*`, `SonosFlow.exe`, carpetas de datos y de copias — se conservan.)*

Aplicación de escritorio para Windows que **transmite todo el audio del PC a un altavoz Sonos por Wi-Fi**, sin la app oficial de Sonos, sin Bluetooth y sin cables.

Cómo funciona: captura el audio del sistema con WASAPI loopback, lo sirve como un stream WAV en la red local y le indica al Sonos (UPnP, puerto 1400) que lo reproduzca. El Sonos descarga el stream. Detalles y mediciones en [docs/INFORME-TECNICO.md](docs/INFORME-TECNICO.md).

## Instalar

1. Ejecuta `artifacts\SonosFlow-Setup.exe`.
2. Pulsa **Instalar**. Se instala para tu usuario en `%LOCALAPPDATA%\Programs\SonosFlow` (sin permisos de administrador) y crea accesos directos en el escritorio y en el menú Inicio.
3. La primera vez que transmitas, Windows puede preguntar por el firewall: permite el acceso **solo en redes privadas**. Es necesario para que el Sonos pueda descargar el audio desde el PC.

Al no estar firmado digitalmente, SmartScreen puede mostrar "Windows protegió su PC": *Más información → Ejecutar de todas formas*.

**Desinstalar:** Configuración → Aplicaciones → Aplicaciones instaladas → Audiora → Desinstalar. Se conservan tus ajustes (`%APPDATA%\SonosFlow`) y registros (`%LOCALAPPDATA%\SonosFlow`).

## Usar

- El Sonos se detecta solo. Si tienes varios, elígelo en la barra lateral ("Mis altavoces").
- Pulsa el botón verde para **transmitir** o **detener**.
- **Silenciar el PC** (activado por defecto): solo suena el Sonos; los altavoces del PC se restauran al detener, al salir o incluso tras un cierre inesperado.
- **Nivel completo**: Windows aplica su volumen al audio capturado; esta opción lo sube al 100 % solo mientras el PC está silenciado, para controlar el volumen desde el Sonos.
- **Volumen, graves, agudos y loudness** del Sonos se controlan desde la app (los aplica el altavoz).
- Al cerrar la ventana la app sigue en la bandeja del sistema (configurable en Ajustes).

## Compilar

Requiere el .NET 8 SDK.

```powershell
dotnet build SonosFlow.sln -c Release      # compilar
.\build.ps1                                 # app autocontenida + instalador en artifacts\
```

## Estructura

| Carpeta | Contenido |
|---|---|
| `src/SonosFlow.Core` | Captura (`Audio/`), servidor HTTP (`Net/`), control y descubrimiento Sonos (`Sonos/`), sesión de streaming con reconexión (`Streaming/`) |
| `src/SonosFlow.App` | Interfaz WPF: tema, visualizador de espectro, bandeja, ajustes, desinstalación |
| `src/SonosFlow.Setup` | Instalador por usuario (.NET Framework 4.8, incluido en Windows) |
| `src/SonosFlow.Backup` | Motor de copias y restauración (sin dependencias) |
| `src/SonosFlow.BackupCli` + `sfbackup.cmd` | Herramienta de línea de comandos para copias |
| `tests/SonosFlow.Backup.Tests` | Pruebas automáticas del sistema de copias (xUnit) |
| `tools/SonosFlow.CaptureCheck` | Comprobación automática del aislamiento de la captura por aplicación (tonos en una salida no audible) |
| `poc/` | Prueba mínima por consola usada para validar el streaming y medir |
| `tools/make_icon.py` | Genera el icono (sin dependencias) |

## Transmitir una sola aplicación

En **Fuente de audio → Una aplicación** aparecen las aplicaciones que tienen audio (icono, nombre y si están sonando; la lista se actualiza sola). Al elegir una, **solo su audio** va al Sonos; los juegos, Discord y el resto siguen sonando por sus salidas habituales. Se puede cambiar de fuente durante la transmisión sin reconectar el Sonos, y volver a **Todo el PC** en cualquier momento.

- Usa la captura por proceso de Windows (Windows 10 2004 o posterior). No instala controladores ni cambia la salida predeterminada.
- **Silenciar el PC funciona en este modo:** la captura por aplicación no depende del silencio ni del volumen de Windows (medido: con la salida silenciada o al 10 % el nivel capturado no cambia). Con «Silenciar el PC» activado, Audiora silencia la salida por la que suena esa aplicación y la restaura al terminar; también puedes silenciar Windows a mano: el Sonos sigue sonando. El volumen de Audiora es el del Sonos y nunca cambia el volumen maestro de Windows en este modo.
- Silenciar la salida afecta a todo lo que suene por ella: si quieres oír el juego o Discord, que usen otra salida (p. ej. los auriculares) que la aplicación que envías al Sonos.
- Lo que **no** sirve es silenciar o bajar **la propia aplicación** en el mezclador de volumen de Windows: eso sí llega a la captura (silencio).
- «Nivel completo» solo se aplica en modo **Todo el PC**, donde la captura sí depende del volumen de Windows.
- **Navegadores:** Chrome, Edge o Brave reproducen el audio de **todas sus pestañas** desde un mismo proceso, así que no se puede enviar una sola pestaña. Para YouTube Music usa **Abrir YouTube Music aislado**: abre music.youtube.com en una ventana con perfil propio (Brave si está instalado; si no, Edge o Chrome), que es un proceso separado y aparece como «YouTube Music (instancia aislada)». La primera vez hay que iniciar sesión en esa ventana.
- Si la aplicación se cierra, la transmisión sigue (en silencio) y se retoma sola cuando vuelve a abrirse.
- Los sonidos del sistema de Windows no se pueden capturar por separado.

## Margen anti-cortes

El Sonos reproduce en directo, así que una pausa corta de la Wi-Fi puede oírse como un corte. En **Ajustes → Audio → Margen anti-cortes** (por defecto 1 s) el Sonos recibe de golpe ese tiempo de audio al conectar y reproduce con ese retraso, aguantando pausas de red más cortas. «Sin margen» = mínima latencia (comportamiento anterior). Para diagnosticar cortes, `stream-*.csv` registra cada segundo el audio que espera a que el Sonos lo recoja (`backlog_ms`) y cada conexión del Sonos. Detalles: [docs/INVESTIGACION-CORTES-6MIN.md](docs/INVESTIGACION-CORTES-6MIN.md).

## Atajos de teclado

Funcionan en todo Windows, también dentro de un juego, y no toman el foco (Ajustes → Mini reproductor → «Atajos de teclado globales», activados por defecto):

| Atajo | Acción |
|---|---|
| Ctrl+Alt+↑ / Ctrl+Alt+↓ | Volumen del Sonos ±5 |
| Ctrl+Alt+P | Pausa / reanuda (o empieza a transmitir) |
| Ctrl+Alt+O | Mostrar / ocultar el mini reproductor |

Las teclas multimedia del teclado no se tocan: siguen controlando el reproductor que suena. Si otro programa ya usa algún atajo, Ajustes lo indica.

## Indicador de red

En **Estadísticas → Red**: «Estable» o el número de atascos (segundos en que la Wi-Fi o el Sonos dejaron de recoger el audio a tiempo) y el peor. En naranja si alguno superó el margen anti-cortes (probable corte audible). Cada atasco queda anotado con su hora en `app-events.log` y en `stream-*.csv`. Los atascos muy cortos los absorbe Windows y no se ven.

## Idioma

En el primer arranque de una instalación nueva, Audiora pide el idioma y sugiere el de Windows. Se puede cambiar cuando quieras en **Ajustes → Idioma**: el cambio es inmediato y no corta la transmisión. Incluye 12 idiomas, árabe (de derecha a izquierda) entre ellos. Las traducciones, salvo la española, son automáticas y pendientes de revisión nativa. Arquitectura y cómo añadir un idioma: [docs/LOCALIZATION.md](docs/LOCALIZATION.md).

## Mini reproductor

Cápsula flotante con la fuente, el estado real, el Sonos, el volumen (el mismo que en la ventana principal), **pausa** (el Sonos sigue conectado y recibe silencio; reanudar es instantáneo), abrir la ventana principal, elegir la fuente y ocultar. Se abre con el botón de la barra de título, desde la bandeja o al minimizar/cerrar (según Ajustes). Se puede arrastrar, recuerda su posición, se mantiene dentro de la pantalla, puede quedarse siempre encima y **nunca toma el foco del teclado** (no interrumpe juegos). Ocultarlo o cambiar de ventana no detiene ni reinicia el audio.

## Copias de seguridad

Copias del código fuente con fecha y hora, verificadas con SHA-256, en `C:\Users\Admin\SonosFlow-Backups` (fuera del proyecto y de OneDrive). Desde la app (**Ajustes → Copias de seguridad**) o desde la terminal:

```bat
sfbackup create "motivo"     & rem nueva copia
sfbackup list                & rem enumerar
sfbackup verify <id>         & rem integridad
sfbackup plan <id>           & rem qué cambiaría (no modifica nada)
sfbackup restore <id>        & rem restaurar con confirmación (antes copia el estado actual)
```

Guía completa: [docs/COPIAS-DE-SEGURIDAD.md](docs/COPIAS-DE-SEGURIDAD.md).

## Privacidad y seguridad

- Todo es local: el audio nunca sale de tu red y no se recopila ningún dato.
- El servidor escucha solo en la IP de la red local y **rechaza cualquier conexión que no venga de un Sonos detectado**.
- Sin permisos de administrador. Solo escribe en tu perfil de usuario y en `HKCU` (inicio con Windows y entrada de desinstalación).
- No modifica el firewall ni el antivirus.

## Limitaciones conocidas

- **Latencia**: el Sonos reproduce ~1–1,5 s por detrás del PC con el margen anti-cortes de 1 s (≈ 0,2 s sin margen); no es apto para juegos ni para vídeo con sincronía labial. No se ha medido con micrófono: el «búfer del Sonos» es una estimación de ±1 s.
- Una sola pestaña del navegador no se puede capturar por separado (ver «Transmitir una sola aplicación»).
- Grupos de Sonos: hay que elegir el altavoz principal del grupo.
- Licencias: NAudio (MIT). Proyecto personal, no afiliado a Sonos, Inc.
