# Rediseño premium de la interfaz (v2)

Fecha: 28/10/2026 · Rama `ui/rediseno-premium` (sin fusionar en `main`). Implementado en WPF con Claude Code; Pen.dev no se usó en esta fase (la fase 0 sigue en `docs/UI_DESIGN_PHASE0.md` y `docs/design/*.png` para comparar).
Capturas de la app real: `docs/design/v2/`.

## 1. Por qué la fase 0 no bastaba
Reorganizaba lo mismo: barra lateral, tarjeta grande + tres tarjetas, estadísticas en rejilla. Mismo esqueleto, otra pintura.

## 2. Dos conceptos y decisión

| | Concepto A — lujo silencioso | Concepto B — audio inmersivo |
|---|---|---|
| Composición | Columna centrada, tipografía grande, controles mínimos | Pieza central visual alrededor de la acción principal, luz tenue al transmitir |
| Realimentación | Texto de estado y un punto de color | Visualización del audio en directo |
| Riesgo | Frío: no transmite «está sonando» | Recargado o decorativo si los datos no son reales |

**Elegido: B contenido.** La estructura y la sobriedad de A, más un único elemento expresivo de B: un **anillo con el espectro real** alrededor del botón de transmisión. Es honesto porque Audiora ya tiene un analizador real (abajo), y responde de un vistazo a «¿está sonando y se está enviando?». Se eliminan la barra lateral, el mosaico de tarjetas y la ilustración del altavoz.

## 3. Nueva dirección visual
- **Paleta:** casi negro (`#060708`) y carbón; verde esmeralda **solo** para «en directo» y la acción principal; ámbar = recuperándose; rojo = error con acción. Gris terciario `#78838B` (4,7:1, antes 3:1).
- **Tipografía:** Segoe UI Variable; el altavoz como titular de 44 px; cifras en Display; rótulos en versalitas atenuadas.
- **Superficies:** sin tarjetas en Inicio; los grupos de Ajustes son una sola superficie con filas y líneas finas; los paneles desplegables tienen una sombra suave. Sin desenfoque de fondo.
- **Movimiento:** solo el anillo (datos reales) y la luz detrás, que sigue el nivel medido; el resto, transiciones cortas ya existentes.

## 4. Composición de Inicio
```
Cabecera 56 px: Audiora · Inicio | Ajustes ············ [● Bedroom ▾] · mini · _ □ ×
                          (aviso rojo con «Reintentar» / «Buscar altavoces» solo en error)
                       Sonos Five · En línea
                            Bedroom                      ← altavoz como titular
                     ● Transmitiendo  /  detalle
                    ◯ anillo de espectro real ◯
                         [ ■ botón ]
                        Detener · 00:12:41
     [carátula] Título — Artista            [Temporizador ▾]
     🔈 ━━━━━━━━━━━━━●──────── 🔊   38
           ◯ Silenciar el PC   ◯ Nivel completo
              ( Todo el PC | Una aplicación )
        [chip YouTube Music ●] [chip Discord] …  · Abrir YouTube Music aislado
Pie 46 px: ● Red Estable · Búfer ≈1,2 s · Envío 192 kB/s · Sonos Reproduciendo     Sonido ▴ · Detalles ▴
```

## 5. Datos de medios y espectro (fase 4)
- **Espectro: real.** `Core/Audio/SpectrumAnalyzer.cs`: FFT de 2 048 puntos sobre el PCM que se envía al Sonos, 48 bandas logarítmicas de 40 Hz a 16 kHz y picos L/R. El nuevo `Controls/RadialSpectrum.cs` lee las mismas bandas con el mismo suavizado que el espectro anterior. Sin transmisión dibuja un anillo de puntos estático: no se anima nada que no exista. Nota: muestra lo **capturado**, que suena en el Sonos ~1–1,5 s después (margen anti-cortes).
- **Título y artista:** ya existían (GSMTC).
- **Carátula:** nueva. `MediaSessionWatcher` lee la miniatura que publica la app (máx. 2 MB, 3 s de espera) y `MainViewModel.Media` la expone como `NowPlayingArtwork`/`HasArtwork`. Se muestra solo si existe; si no, se ve un icono de nota.

## 6. Archivos cambiados

| Archivo | Propósito |
|---|---|
| `src/SonosFlow.App/Theme/Theme.xaml` | Paleta nueva y estilos: `T.Hero`, `T.Kicker`, `T.Value`, `Surface.Group`, `Hairline`, `Flyout`, `Btn.Pill`, `Btn.Quiet`, `Nav.Tab`, `ChipButton`, `Slider.Volume`, `SegTrack`, `StateDot`; `Seg` sin verde. Claves anteriores conservadas |
| `src/SonosFlow.App/MainWindow.xaml` | Ventana reescrita (cabecera, Inicio, pie, paneles); mismos nombres usados por el código |
| `src/SonosFlow.App/MainWindow.xaml.cs` | Quitada la animación de anillos; cierre del menú de altavoces al elegir |
| `src/SonosFlow.App/Views/SettingsView.xaml(.cs)` | Ajustes como vista propia, agrupados; copias del código en «Avanzado» |
| `src/SonosFlow.App/Controls/RadialSpectrum.cs` | Anillo con el espectro real |
| `src/SonosFlow.App/MiniPlayerWindow.xaml(.cs)` | Cápsula 600×76 sólida, carátula en el anillo de nivel; posición inicial recalculada |
| `src/SonosFlow.App/VolumeHudWindow.cs` | Mismo fondo que el mini |
| `src/SonosFlow.App/Infrastructure/MediaSessionWatcher.cs`, `ViewModels/MainViewModel.Media.cs` | Carátula (agente de integración con Windows, commit `fa3c2d2`) |
| `src/SonosFlow.App/ViewModels/MainViewModel.Sources.cs` | **Corrección de un fallo previo**: dos procesos raíz de la misma app (p. ej. dos perfiles de Brave) hacían saltar `ArgumentException` cada 2,5 s en la lista de apps |

No se tocó: `SonosFlow.Core` (audio, red, Sonos, sesión), ajustes guardados, instalador ni dependencias.

## 7. Verificación

| Comprobación | Resultado | Tipo |
|---|---|---|
| `dotnet build SonosFlow.sln -c Release` | 0 avisos, 0 errores | automática |
| `dotnet test SonosFlow.sln -c Release` | 42/42 | automática |
| Inicio a 1280×840 y 1080×720, Ajustes, mini (desplegado), HUD | Capturas de la **app real** en `docs/design/v2/`; sin `errors.log` nuevo | visual (app en ejecución) |
| Estado «sin altavoz» | Visto en la app real (la zona Wi-Fi estaba apagada) | visual |
| Anillo con datos | `RadialSpectrum` + `SpectrumAnalyzer` reales alimentados con una **señal sintética** en un programa aparte (`anillo-espectro-senal-sintetica.png`) | simulada |
| Transmitiendo, reconectando y error con el Sonos; carátula con YouTube Music sonando | **No verificado**: el Sonos no era accesible | pendiente |
| Revisión independiente | ver §9 | revisión |

**Pasos exactos para verificar con el Sonos** (zona Wi-Fi encendida, Audiora cerrado, nada sonando):
1. `dotnet build SonosFlow.sln -c Release`.
2. Autotest: `tools\SonosFlow.CaptureCheck\bin\Release\net8.0-windows\SonosFlow.CaptureCheck.exe tone-quiet 300 300 "Steam Streaming"` y `src\SonosFlow.App\bin\Release\net8.0-windows10.0.19041.0\SonosFlow.exe --selftest-session <pid> --selftest-out %TEMP%\st.txt` → 26/26.
3. Abrir esa misma `SonosFlow.exe`, transmitir YouTube Music aislado y comprobar: anillo moviéndose con la música, luz tenue, título, artista y carátula, botón «Detener», pie con «Red Estable», panel «Sonido» y «Detalles».
4. Apagar la Wi-Fi del Sonos un momento: punto y botón en ámbar («Reconectando…»); con el Sonos apagado: aviso rojo con acciones.
5. Mini reproductor (Ctrl+Alt+O) encima de un juego en ventana sin bordes: no roba el foco.

## 8. Limitaciones conocidas
- La luz y el anillo reflejan lo capturado, antes de que suene en el Sonos: ≈ 1,1–1,4 s antes (estimación) con el margen anti-cortes por defecto de 1 s, menos con un margen menor (ajustable de 0 a 1000 ms).
- A 1080×720 Inicio se desplaza (la fuente queda bajo el pliegue).
- El mini reproductor sigue sin poder usarse con teclado (a propósito: nunca toma el foco); se controla con ratón y atajos.
- Los paneles desplegables (`Popup` + `StaysOpen=False`) pueden necesitar dos clics para reabrirse justo después de cerrarse (comportamiento estándar de WPF).
- `Controls/SpectrumVisualizer.cs` (barras) ya no se usa; se conserva por si se quiere en Ajustes o en un panel.
- El menú de la bandeja sigue siendo el nativo de WinForms.

## 9. Revisión independiente
Agente `audiora-code-reviewer` sobre `189dd4f..9d7eb5f` (solo lectura). **Nada crítico**: todos los enlaces del diseño anterior siguen presentes, todas las rutas nuevas existen y notifican, los nombres que usa el código siguen ahí, los controles de la cabecera responden dentro de la franja de arrastre, y el anillo y la carátula son correctos en hilos y recursos. Corregido después (commit siguiente):
- Los errores secundarios (`LastErrorText`: «No se encontró Brave…», ajuste rechazado por el Sonos) vuelven a verse en Inicio como aviso ámbar discreto.
- Paneles desplegables: el clic en su propio botón ahora los cierra.
- Ayudas que no aparecían (anillo, interruptores deshabilitados); signo en graves y agudos (`+3`); texto de error duplicado; «Nivel completo» con el mismo criterio en Inicio y Ajustes.

Sin corregir (bajo): con Ajustes abierto el «Pico» de «Detalles» se congela hasta volver a Inicio; la carátula se decodifica en el hilo de la interfaz (≤ 256 px); con dos perfiles del mismo navegador sonando a la vez, la elección puede alternar.

## 10. Trabajo pendiente
1. Verificación con el Sonos (§7).
2. Respetar «Mostrar animaciones» de Windows (pausar la luz).
3. Indicador de aislamiento por aplicación (mejora futura de la auditoría §4).
4. Fusionar `ui/rediseno-premium` en `main` e instalar, tras la verificación.
