# Sonos Flow — prueba mínima de streaming

Cadena: **WASAPI loopback → servidor HTTP local (WAV 16 bit, estéreo) → Sonos vía UPnP `SetAVTransportURI` + `Play`.**
El Sonos *descarga* el stream desde el PC; no se le "empuja" audio.

## Ejecutar

```powershell
cd SonosFlow.Poc
dotnet build -c Release
.\bin\Release\net8.0\SonosFlow.Poc.exe --list            # solo detectar altavoces
.\bin\Release\net8.0\SonosFlow.Poc.exe --duration 600    # transmitir 10 min (Ctrl+C para parar)
```

Opciones: `--name <texto>` (sala o modelo, por defecto `Five`), `--port 8780`, `--duration <s>`, `--volume <0-100>`,
`--mute-local` (silencia los altavoces del PC mientras transmite y restaura el estado anterior al terminar).

Comprobado en este PC (NVIDIA HD Audio → BenQ EX270QM): con la salida silenciada, la captura loopback sigue recibiendo la señal.
Depende del controlador, así que en otro equipo hay que verificarlo mirando `peak_dbfs` en el CSV.

Cada ejecución deja un CSV por segundo en `logs\` (estado del Sonos, kB/s enviados, audio capturado frente a silencio, pico en dBFS, descartes, reconexiones).

## Módulos

| Carpeta | Responsabilidad |
|---|---|
| `Discovery/` | SSDP M-SEARCH (ZonePlayer) + descripción del dispositivo |
| `Audio/` | Captura WASAPI loopback, conversión a PCM 16 bit, relleno de silencio en tiempo real |
| `Net/` | Servidor HTTP mínimo (TcpListener, sin permisos de admin), solo acepta IPs de Sonos |
| `Control/` | SOAP UPnP: AVTransport (URI/Play/Stop/estado) y RenderingControl (volumen) |

## Seguridad

- El servidor escucha solo en la IP LAN que alcanza al Sonos, no en todas las interfaces, y rechaza cualquier IP que no sea un Sonos detectado.
- No se envía nada a Internet. No requiere administrador.
