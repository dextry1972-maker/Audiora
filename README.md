# Sonos Flow

Aplicación de escritorio para Windows que **transmite todo el audio del PC a un altavoz Sonos por Wi-Fi**, sin la app oficial de Sonos, sin Bluetooth y sin cables.

Cómo funciona: captura el audio del sistema con WASAPI loopback, lo sirve como un stream WAV en la red local y le indica al Sonos (UPnP, puerto 1400) que lo reproduzca. El Sonos descarga el stream. Detalles y mediciones en [docs/INFORME-TECNICO.md](docs/INFORME-TECNICO.md).

## Instalar

1. Ejecuta `artifacts\SonosFlow-Setup.exe`.
2. Pulsa **Instalar**. Se instala para tu usuario en `%LOCALAPPDATA%\Programs\SonosFlow` (sin permisos de administrador) y crea accesos directos en el escritorio y en el menú Inicio.
3. La primera vez que transmitas, Windows puede preguntar por el firewall: permite el acceso **solo en redes privadas**. Es necesario para que el Sonos pueda descargar el audio desde el PC.

Al no estar firmado digitalmente, SmartScreen puede mostrar "Windows protegió su PC": *Más información → Ejecutar de todas formas*.

**Desinstalar:** Configuración → Aplicaciones → Aplicaciones instaladas → Sonos Flow → Desinstalar. Se conservan tus ajustes (`%APPDATA%\SonosFlow`) y registros (`%LOCALAPPDATA%\SonosFlow`).

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
| `poc/` | Prueba mínima por consola usada para validar el streaming y medir |
| `tools/make_icon.py` | Genera el icono (sin dependencias) |

## Privacidad y seguridad

- Todo es local: el audio nunca sale de tu red y no se recopila ningún dato.
- El servidor escucha solo en la IP de la red local y **rechaza cualquier conexión que no venga de un Sonos detectado**.
- Sin permisos de administrador. Solo escribe en tu perfil de usuario y en `HKCU` (inicio con Windows y entrada de desinstalación).
- No modifica el firewall ni el antivirus.

## Limitaciones conocidas

- **Latencia**: el Sonos acumula audio antes de reproducir; no es apto para juegos competitivos. La latencia real aún no se ha medido con micrófono; la app la muestra como "Sin medir".
- Captura por aplicación individual: todavía no (se captura un dispositivo de salida completo).
- Grupos de Sonos: hay que elegir el altavoz principal del grupo.
- Licencias: NAudio (MIT). Proyecto personal, no afiliado a Sonos, Inc.
