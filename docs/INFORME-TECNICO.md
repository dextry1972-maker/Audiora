# Sonos Flow — Informe técnico (fases 1 y 2)

Estado: **streaming validado en el Sonos Five real** (el usuario confirmó que se oye), prueba larga superada y app con instalador terminada. Pendiente: latencia acústica con micrófono.

## 1. Cómo puede recibir audio un Sonos Five desde Windows

| Vía | ¿Viable? | Notas |
|---|---|---|
| Empujar PCM/RTP al altavoz | **No** | Sonos no expone un receptor de audio "push" documentado. |
| **UPnP AVTransport + stream HTTP** (elegida) | **Sí, verificado** | El PC sirve un stream; se le indica al Sonos con `SetAVTransportURI` + `Play` (SOAP, puerto 1400) y **el Sonos lo descarga**. Es lo que usa swyh-rs. |
| AirPlay 2 | Sí (el Five lo soporta) | Emisores para Windows de pago o poco mantenidos; latencia nominal ~2 s. Alternativa futura, no necesaria. |
| API Cloud de Sonos | No | Pasa por Internet; requisito: todo local. |
| Entrada de línea 3,5 mm | Descartada | Requisito: sin cables. |

Control (volumen, estado, detección SSDP) usa las mismas interfaces UPnP locales. No se necesita la app oficial.

## 2. Arquitectura validada

```
WASAPI loopback (48 kHz float) → conversión a PCM 16 bit estéreo
   → relleno de silencio en tiempo real (WASAPI no entrega paquetes cuando no suena nada)
   → servidor HTTP propio (TcpListener, IP LAN, lista blanca de IPs Sonos)
   → Sonos descarga WAV "infinito"  ←  control SOAP (URI, Play, Stop, estado, volumen)
```

Formato: WAV LPCM 48 kHz / 16 bit / estéreo = 1536 kbit/s (192 kB/s). Sin compresión → sin latencia de códec y sin pérdida de calidad perceptible.

## 3. Resultados medidos (no estimados salvo donde se indica)

| Prueba | Resultado |
|---|---|
| Detección SSDP | Sonos Five "Bedroom" (S24), 192.168.137.158, firmware Sonos/96.1 |
| El Sonos descarga el stream | Sí — `GET /sonosflow.wav`, UA `Linux UPnP/1.0 Sonos/96.1-79270 (ZPS24)` |
| Reproducción | `PLAYING` continuo, 0 descartes, 0 reconexiones (60 s y 25 s) |
| Ritmo de envío | 188–202 kB/s por segundo (nominal 192 kB/s) |
| Audio audible | Confirmado por el usuario |
| `--mute-local` | Con la salida del PC silenciada la captura sigue recibiendo señal; estado restaurado al salir |
| Volumen de Windows | **La captura es post-volumen**: con Windows al 36 % (−15,5 dB) la señal llega 15,5 dB más baja |
| Búfer del Sonos (**estimación**, ±1 s) | ≈ 0,5 s entre audio entregado y posición reportada (`RelTime`) |
| Latencia acústica | Pendiente: el micrófono del HS80 entregaba silencio (apagado/silenciado) |
| **Prueba larga (25 min, detenida a petición del usuario)** | 1470/1470 lecturas en `PLAYING`; 0 descartes, 0 reconexiones; 180–215 kB/s (media 195,9); bytes enviados = esperados ±0,006 % (sin deriva de reloj); ~14 min de audio real + silencio; búfer estimado 0,1–1,1 s |
| **App final (WPF)** | Transmisión iniciada desde la app: `PLAYING` a los ~3 s, ~194 kB/s, 0 descartes; PC silenciado durante la transmisión y restaurado al cerrar; la app no modifica volumen ni ecualizador del Sonos salvo que el usuario lo toque |
| Instalador | Instalación de prueba en carpeta temporal: 466 archivos en ~1 s, la app instalada arranca y dibuja la interfaz |

### Fallos encontrados y corregidos

1. **Silencio intercalado con audio** (se enviaba ~375 kB/s, el doble). NAudio entrega la captura en ráfagas de ~50 ms y el relleno de silencio saltaba entre ráfagas. Corregido contando muestras emitidas frente al reloj real con margen de 120 ms.
2. **Detector de pitidos** con umbral absoluto demasiado alto para una señal atenuada por el volumen de Windows. Corregido con umbral bajo + criterio de tonalidad (Goertzel).

## 4. Limitaciones conocidas (honestas)

- **Latencia**: Sonos almacena audio antes de reproducir. Para videojuegos no será instantáneo por Wi-Fi. Valor real pendiente de medir.
- **Nivel**: depende del volumen de Windows (captura post-volumen en este equipo). Propuesta: opción que fije el volumen de Windows al 100 % mientras está silenciado y lo restaure.
- **Silencio local**: depende del controlador; en este PC funciona.
- **Multihabitación**: no se promete sincronía; un grupo Sonos debe controlarse a través del coordinador.
- **Red**: el Sonos está en la zona Wi-Fi compartida del PC (192.168.137.x); si el PC se apaga, el Sonos pierde también esa red.

## 5. Decisiones para la aplicación (fase 3)

| Decisión | Elección | Motivo |
|---|---|---|
| Lenguaje/runtime | C# / .NET 8 | Ya validado; NAudio (MIT) estable; sin dependencias nativas extra |
| Interfaz | **WPF + fondo Mica (DWM) en Windows 11** | Madura, animaciones por GPU, bajo consumo, se distribuye como un solo ejecutable; WinUI 3 añade el runtime de Windows App SDK y empaquetado más complejo |
| Módulos | `Core.Audio`, `Core.Streaming`, `Core.Sonos`, `App` (WPF) | Separación pedida; el PoC ya tiene esa forma |
| Audio por aplicación | Process loopback (`ActivateAudioInterfaceAsync`, Windows 10 2004+) | Viable sin controlador virtual; se implementará tras la app base |
| Formato | WAV 16 bit; FLAC como opción si la Wi-Fi da cortes | FLAC reduce ancho de banda, añade algo de latencia |
| Instalador | `dotnet publish` autocontenido + instalador (Inno Setup o MSIX) | Se decidirá en fase 5; pedirá confirmación antes de instalar herramientas |

## 6. Fuentes

- swyh-rs: https://github.com/dheijl/swyh-rs
- SoCo (control Sonos): https://soco.readthedocs.io/
- Sonos HLS docs: https://docs.sonos.com/docs/http-live-streaming-hls
- AirConnect: https://hub.docker.com/r/iflip721/airconnect
