# Auditoría para el rediseño de la interfaz (Pen.dev + Claude Code)

Fecha: 28/10/2026 · Rama `main` en `189dd4f` · Árbol limpio antes de la auditoría.
Alcance: solo inspección. **No se ha cambiado la interfaz, el motor de audio ni ningún comportamiento**, ni se ha instalado nada. Las afirmaciones marcadas **[verificado]** se comprobaron ejecutando algo hoy; el resto se deduce del código.

---

## 1. Arquitectura actual

| Pieza | Tecnología | Responsabilidad |
|---|---|---|
| `SonosFlow.Core` | .NET 8 (`net8.0`), NAudio.Wasapi 2.2.1 (única dependencia) | Captura (WASAPI loopback y por proceso), servidor HTTP WAV, control UPnP del Sonos, sesión de streaming con vigilante y reintentos. **Sin referencias a la interfaz.** |
| `SonosFlow.App` | WPF + WinForms (solo `NotifyIcon`), `net8.0-windows10.0.19041.0` (por GSMTC) | Ventanas, ViewModels, ajustes, bandeja, atajos, HUD, título/artista |
| `SonosFlow.Setup` | .NET Framework 4.8, WinForms | Instalador por usuario |
| `SonosFlow.Backup(.Cli)` | .NET 8 | Copias del código fuente (herramienta de desarrollo) |
| `tests/*`, `tools/CaptureCheck` | xUnit, consola | 42 pruebas automáticas; pruebas de captura locales |

Detalle técnico completo: `docs/ARCHITECTURE.md`, `docs/AUDIO_PIPELINE.md`, `docs/NETWORKING.md`.

**Patrón de la interfaz:** MVVM ligero propio (`Infrastructure/Mvvm.cs`: `ObservableObject`, `RelayCommand`), sin frameworks externos. Un único `MainViewModel` (5 archivos parciales, ~1 250 líneas) es el `DataContext` de **todas** las ventanas: una sola sesión y un solo estado compartido.

**Entrada:** `App.xaml.cs` → instancia única (mutex) → `AppSettings.Load()` → `MainViewModel` → `MainWindow` + `MiniPlayerWindow` (+ `VolumeHudWindow` bajo demanda) → `TrayIcon` → `GlobalHotkeys` → `InitializeAsync` (descubrimiento y, si procede, espera de arranque).

**Ajustes y persistencia:** `%APPDATA%\SonosFlow\settings.json` (`AppSettings`, se guarda en cada cambio; los modos de prueba no guardan). Registros en `%LOCALAPPDATA%\SonosFlow\logs` (`app-events.log`, `stream-*.csv`). Inicio con Windows y desinstalación en `HKCU`.

**Compilación y pruebas:** `dotnet build SonosFlow.sln -c Release` · `dotnet test SonosFlow.sln -c Release` (42/42 en `189dd4f`) · `build.ps1` (instalador) · capturas `--screenshot/--screenshot-mini/--screenshot-hud` · autotest con el Sonos `--selftest-session` (26 comprobaciones). Ver `docs/TESTING.md`.

---

## 2. Mapa de la interfaz actual

### 2.1 Ventanas

| Ventana | Archivo | Tamaño | Notas técnicas |
|---|---|---|---|
| Principal | `MainWindow.xaml` (1 012 líneas) + `.xaml.cs` | 1280×840, mínimo 1080×720 | `WindowChrome` propio (barra de título personalizada), esquinas y modo oscuro por DWM. Dos «páginas» (`HomePage`, `SettingsPage`) que el código intercambia con una animación de 260–320 ms. |
| Mini reproductor | `MiniPlayerWindow.xaml` (286) + `.xaml.cs` | cápsula 760×100 + selector desplegable | `AllowsTransparency` (ventana por capas, se dibuja por software), `WS_EX_NOACTIVATE|TOOLWINDOW` (**nunca toma el foco**), `Topmost` configurable, arrastrable, atenuación en reposo, anillo de nivel a ~15 fps. |
| HUD de volumen | `VolumeHudWindow.cs` (117, sin XAML) | se ajusta al contenido | Sin foco y transparente al ratón; abajo al centro del monitor del ratón; se oculta a los 1,6 s. No se ve sobre juegos en pantalla completa exclusiva. |
| Bandeja | `Infrastructure/TrayIcon.cs` | menú WinForms | Estado, abrir, mini, transmitir/detener, salir. **No usa el tema** (menú nativo de WinForms). |

### 2.2 Página «Inicio» (de arriba abajo)

| Bloque | Contenido y enlaces |
|---|---|
| **Barra lateral** | Logo/nombre; navegación `Inicio`/`Ajustes` (`NavigateCommand`, estilo `Nav`); «Mis altavoces» (`Speakers`, `SelectSpeakerCommand`, `IsOnline`, `IsStreaming` con mini ecualizador animado); «Red local / Buscar altavoces» (`RescanCommand`). |
| **Barra de título** | Botón mini reproductor (`ToggleMiniCommand`), minimizar/maximizar/cerrar (código). |
| **Tarjeta principal (hero)** | Ilustración vectorial del altavoz con LED (`IsActive`); nombre/modelo/conexión (`SelectedSpeaker.*`, `SpeakerOnline`, `ConnectionText`); estado (`StatusTitle`, `StatusDetail`); **botón circular Transmitir/Detener** 104 px con anillos animados (`ToggleStreamCommand`, `IsStreaming`, `IsActive`); `ElapsedText`; **temporizador** (`SleepChoices`, `SleepKey`, `SleepRemainingText`); **volumen del Sonos** 40 px + deslizador (`Volume`, `ControlsReady`); interruptores **Silenciar el PC** (`MuteLocal`, `LocalOptionsEnabled`) y **Nivel completo** (`FullLevel`, `FullLevelEnabled`). |
| **Audio en tiempo real** | `SpectrumVisualizer` (control propio, `Analyzer`), pico (`PeakDbfs` por `ElementName=Viz`). |
| **Estadísticas** | Sonos (`TransportText`), tiempo, velocidad (`RateText`), búfer (`BufferText`), descartes/reconexiones, **Red** (`NetworkText`, `NetworkWarning` → naranja), formato. |
| **Fuente de audio** | Segmentado «Todo el PC / Una aplicación» (`IsAppMode`); dispositivo (`Outputs`, `SelectedOutput`); lista de apps con icono y estado (`Apps`, `SelectAppCommand`, plantilla `AppRowButton`); vacío («Ninguna aplicación…»); `SourceName · SourceStatusText`; «Abrir YouTube Music aislado» (`LaunchYouTubeMusicCommand`); **Ahora suena** (`HasNowPlaying`, `NowPlayingText`); `SourceFormatText`. |
| **Ecualizador del Sonos** | Graves/agudos (−10…+10), loudness, restablecer (`Bass`, `Treble`, `Loudness`, `ResetEqCommand`, `ControlsReady`). |
| **Conexión** | Altavoz, IP, estado, URL del stream, último error. |

### 2.3 Página «Ajustes»

General (inicio con Windows, al cerrar, reanudar al abrir) · Mini reproductor (mostrar, encima, recordar posición, al minimizar, atenuar, **atajos globales** con aviso de conflicto) · Audio (silenciar el PC, **margen anti-cortes**, nivel completo) · **Copias de seguridad** (herramienta de desarrollo: lista, crear, verificar, restaurar — `BackupViewModel`) · Diagnóstico (carpeta de registros) · Acerca de (versión).

### 2.4 Sistema de estilos (`Theme/Theme.xaml`, 505 líneas)

- **Colores** (`C.*` y pinceles `B.*`): fondo `#07090B`/`#0C1013`, tarjeta `#11161A`/`#161C21`, trazo `#1E262C`/`#2A343B`, texto `#F1F4F6`/`#8B959D`/`#59636A`, acento `#31D67B`/`#4FEA94`/`#139A52`, aviso `#F2B544`, peligro `#FF5F5F`, cristal 5 %/9 % blanco, degradados de acento y de tarjeta.
- **Tipografía:** Segoe UI Variable Display/Text; iconos Segoe Fluent Icons (glifos `&#xE…;`). Estilos `T.Base` 13, `T.Title` 26 SemiBold, `T.Section` 15 SemiBold, `T.Dim`, `T.Caps` 11, `T.Icon` 16.
- **Superficies:** `Card` (radio 18, relleno 20, sombra `Fx.Card`), `InnerCard` (radio 12).
- **Controles:** `FocusRing`, `Btn.Ghost`, `Btn.Accent`, `Btn.Caption(Close)`, `Nav`, `Switch`, `Slider.Flow`, `Combo.Flow`, `Seg`, `AppRowButton`, `ActiveDot`.
- **Fuera del tema** (deuda que el rediseño debe absorber): estilos locales en `MainWindow.Resources` (fila de altavoz, ficha de estadística), colores literales en el mini reproductor (`#FF9BA6AE`, `#22FFFFFF`…), el HUD construido en código y el menú de bandeja de WinForms.

---

## 3. Funcionalidad que debe conservarse

1. Transmisión **Todo el PC** y **Una aplicación**, con cambio de fuente **en caliente** sin reconectar.
2. Descubrimiento SSDP, selección de altavoz, recuerdo del último, «Buscar altavoces».
3. Volumen, graves, agudos y loudness **del Sonos** (aplicados por el altavoz).
4. **Silenciar el PC** y **Nivel completo**, con su restauración al detener, al salir y tras un cierre inesperado.
5. Pausa (envía silencio, sin desconectar) y reanudación.
6. Margen anti-cortes, vigilante, reintentos acotados, retirada cuando el usuario usa el Sonos desde la app de Sonos (`Released`), espera de arranque.
7. Mini reproductor que **nunca toma el foco**, arrastrable, siempre encima, posición recordada.
8. HUD sin foco; atajos globales Ctrl+Alt+↑/↓/P/O y su aviso de conflicto.
9. Temporizador de apagado; título y artista (GSMTC) con su regla de «nada antes que algo erróneo».
10. Bandeja, instancia única, inicio minimizado con Windows, acción al cerrar, desinstalación.
11. Ajustes existentes en `settings.json` (todas las propiedades nuevas con valor por defecto).
12. Modos de prueba y autotest: dependen de nombres concretos (ver §8).
13. Textos en español, nombre visible **Audiora**, identificadores técnicos **SonosFlow**.

---

## 4. Aislamiento de una aplicación y volúmenes independientes (fase 5)

### 4.1 Qué está comprobado hoy **[verificado]**

Con `tools/SonosFlow.CaptureCheck` (tonos en la salida «Steam Streaming», nadie los oye; sin Sonos) sobre la compilación actual:

| Comprobación | Resultado |
|---|---|
| Captura de la app A con A y B sonando | Llega A (−12 dBFS); B no (−91,9 dBFS) → **el aislamiento entre procesos funciona** |
| Salida de Windows **silenciada** | La captura de la app sigue a −12 dBFS → **el Sonos sigue sonando** |
| Volumen maestro de Windows al 10 % | Captura de la app Δ +0,0 dB → **el volumen local no afecta al Sonos** en modo «Una aplicación» |
| Lo mismo en modo «Todo el PC» | Δ −34,7 dB → en este modo **el volumen de Windows sí llega al Sonos** (por eso existe «Nivel completo») |
| App **silenciada en el mezclador de Windows** | La captura recibe silencio (−163 dBFS) → **el Sonos también se queda mudo** |

Y con el Sonos real (autotest de hoy, 26/26): en modo aplicación con «Silenciar el PC», la salida de la app queda silenciada en Windows, el Sonos recibe su audio y el volumen maestro de Windows no se toca.

### 4.2 El problema real

«Una aplicación» aísla **lo que se envía** al Sonos, pero **no lo que suena en el PC**:

- La app capturada sigue sonando en su salida local. «Silenciar el PC» la calla silenciando **toda la salida** (auriculares, monitor…), así que también calla Discord o el juego que usen esa misma salida.
- No hay forma de bajar **solo esa app en local** sin bajarla también en el Sonos: el volumen/silencio de la app en el mezclador de Windows actúa **antes** de la captura.
- Los navegadores mezclan todas sus pestañas en un proceso (de ahí «YouTube Music aislado»).

**Conclusión para el diseño:** con la arquitectura actual existen dos volúmenes independientes **[verificado]**: el **del Sonos** (UPnP) y el **de la salida local** (Windows, afecta a todo lo que suena en ella). **No existe** un volumen local por aplicación independiente del Sonos, y el rediseño no debe mostrar un control que lo sugiera.

### 4.3 Solución sin tocar el motor (recomendada para la interfaz)

Windows permite enviar cada aplicación a una salida distinta (Configuración → Sistema → Sonido → Mezclador de volumen → salida por aplicación). La captura por proceso recoge el audio de la app **sea cual sea su salida** — es exactamente lo que hacen las pruebas, que reproducen en «Steam Streaming» y el Sonos recibe el tono **[verificado]**. Por tanto: enviar YouTube Music a una salida que nadie escucha (en este PC existe «Steam Streaming Speakers») = suena solo en el Sonos, y Discord/juegos siguen en los auriculares, **sin silenciar nada**.

Propuesta de interfaz (fase 5 del plan, requiere añadir propiedades al ViewModel, no al motor):
- Indicador de aislamiento en la tarjeta de fuente: «YouTube Music también suena en *Auriculares*» / «Se silenciará *Auriculares* (también Discord y juegos en esa salida)» / «Solo suena en el Sonos».
- Enlace «Cómo separar el audio» que abra `ms-settings:apps-volume` con una explicación breve.
- Hacerlo automático (cambiar la salida de una app por programa) usaría una API no documentada de Windows: **fuera de alcance** y requeriría autorización explícita.

---

## 5. Componentes aptos para rediseño

| Componente | Tipo de cambio | Riesgo |
|---|---|---|
| Paleta, tipografía, radios, sombras (`Theme.xaml`) | Solo estilo | Bajo |
| Tarjetas, interruptores, deslizadores, combos, segmentado, botones fantasma | Solo estilo (plantillas en el tema) | Bajo |
| Ficha de estadística, fila de ajuste, cabecera de sección | Extraer a estilos reutilizables | Bajo |
| Barra lateral y lista de altavoces | Estilo + estructura ligera | Bajo |
| Tarjeta principal (hero), botón de transmitir, volumen | Estructura (orden, jerarquía) | Medio: concentra estados y animaciones |
| Fuente de audio (segmentado, lista de apps, ahora suena) | Estructura + estados vacíos | Medio |
| Estadísticas/diagnóstico | Reubicar (p. ej., panel plegable «Diagnóstico») | Bajo |
| Ajustes (secciones) | Estructura; «Copias de seguridad» a una sección «Avanzado» | Bajo |
| Mini reproductor | Estilo + estructura | **Medio-alto**: ventana por capas, sin foco, tamaño fijo, selector desplegable |
| HUD | Estilo (pasar a XAML con el tema) | Bajo |
| Menú de bandeja | Solo textos/orden (WinForms no admite el tema sin trabajo extra) | Bajo |
| Ilustración del altavoz | Reemplazable (vector propio, no de Sonos) | Bajo |

**Necesitan cambios estructurales:** `MainWindow.xaml` (1 012 líneas en un archivo) → dividir en controles de usuario por tarjeta y sección, manteniendo el mismo `DataContext`; el mini reproductor y el HUD → mover colores literales a tokens del tema.

---

## 6. Límites entre interfaz, estado de la aplicación y servicios

### 6.1 Lo que no debe cambiar
`SonosFlow.Core` completo (captura, servidor, Sonos, sesión, vigilante), `LocalOutputGuard`, `AppSettings` (formato), `GlobalHotkeys`, `MediaSessionWatcher`, `WindowsIntegration`, `SelfTest`.

### 6.2 Contrato entre interfaz y motor (lo que la interfaz consume)
- **Comandos:** `ToggleStreamCommand`, `PauseResumeCommand`, `RescanCommand`, `SelectSpeakerCommand`, `NavigateCommand`, `UseSystemCommand`, `UseAppModeCommand`, `SelectAppCommand`, `LaunchYouTubeMusicCommand`, `ToggleMiniCommand`, `ToggleMiniSourcesCommand`, `ShowMainCommand`, `ResetEqCommand`, `OpenLogsCommand`, `RefreshOutputsCommand` + los de `BackupViewModel`.
- **Estado:** `State` (`Discovering`, `NoSpeaker`, `Ready`, `Starting`, `Streaming`, `Recovering`, `Error`), `IsStreaming`, `IsActive`, `IsPaused`, `StatusTitle`, `StatusDetail`, `MiniStatusText`, `SpeakerOnline`, `ControlsReady`, `IsAppMode`, `SourceName`, `SourceStatusText`, `HasNowPlaying`, `NowPlaying*`, `Volume`, `Bass`, `Treble`, `Loudness`, `MuteLocal`, `FullLevel`, `LocalOptionsEnabled`, `FullLevelEnabled`, `CanEditOptions`, `NetworkText`, `NetworkWarning`, `Sleep*`, `Hotkeys*`, estadísticas (`*Text`), `Analyzer`.
- **Eventos hacia la App:** `StreamingChanged` (bandeja), `ShowMainRequested`, `HudRequested(icono, texto, fracción)`, `HotkeysSettingChanged`, `PropertyChanged(MiniVisible)`.

### 6.3 Dependencias entre estado de la interfaz y del streaming
- Las opciones de captura (silenciar, nivel completo, margen, dispositivo) **solo se aplican al iniciar** (`CanEditOptions = !IsStreaming`); la interfaz debe mostrarlas bloqueadas durante la transmisión.
- `ControlsReady` llega tras el primer sondeo del Sonos (≤ 3 s); hasta entonces volumen y ecualizador deben verse deshabilitados.
- Cambiar de fuente durante la transmisión es instantáneo salvo cambio de frecuencia (reinicia la sesión).
- `Released` (el usuario usó el Sonos desde su app) termina sin error: el diseño necesita un estado «Detenido por el Sonos», distinto de «Error».
- El audio del Sonos va ~1–1,5 s por detrás (margen anti-cortes): el espectro y el anillo muestran lo **capturado**, no lo que suena en ese instante.

### 6.4 Separación propuesta (sin frameworks nuevos)
La separación ya existe en lo esencial: Core no conoce la interfaz y la interfaz solo habla con `MainViewModel`. Para el rediseño basta con:
1. **Presentación:** dividir el XAML en `UserControl`s (`HeroCard`, `SourceCard`, `StatsCard`, `EqCard`, `ConnectionCard`, secciones de Ajustes) que reciben el mismo `MainViewModel`. Sin cambios de comportamiento.
2. **Estado:** opcionalmente, una propiedad `UiPhase` derivada (p. ej. `Idle`, `Connecting`, `Live`, `Paused`, `Reconnecting`, `WaitingForSpeaker`, `ReleasedBySonos`, `Error`) para que el diseño enlace estados visuales sin lógica en XAML. Es una proyección de propiedades existentes, no una máquina de estados nueva.
3. **Servicios:** mantener. Si `MainViewModel` sigue creciendo, extraer más adelante un `StreamingController` (inicio/parada/espera de arranque); **no es necesario para el rediseño**.

No se recomienda migrar a WinUI 3, MAUI ni añadir CommunityToolkit u otras bibliotecas: el rediseño es visual y WPF lo cubre.

---

## 7. Especificación de diseño para Pen.dev

### 7.1 Dirección visual
Oscuro premium, jerarquía tipo Apple (un foco por pantalla: el estado y el botón de transmitir), controles de audio sobrios inspirados en altavoces (botón circular, volumen grande, deslizadores finos), acento verde **sutil** (solo estado activo y acción principal), espaciado generoso, animación contenida. Sin logotipos, iconos ni ilustraciones de Sonos ni de Apple.

### 7.2 Tokens (variables en Pen.dev → recursos de `Theme.xaml`)

| Token | Valor actual | Propuesta | Recurso WPF |
|---|---|---|---|
| `bg/base` | `#07090B` | igual | `C.Bg0` |
| `bg/raised` | `#0C1013` | igual | `C.Bg1` |
| `surface/card` | `#11161A` | igual | `C.Card` |
| `surface/card-hi` | `#161C21` | igual | `C.CardHi` |
| `stroke/subtle` · `stroke/strong` | `#1E262C` · `#2A343B` | igual | `C.Stroke` · `C.StrokeHi` |
| `text/primary` | `#F1F4F6` | igual | `C.Text` |
| `text/secondary` | `#8B959D` (≈ 6,0:1 sobre tarjeta) | igual | `C.TextDim` |
| `text/tertiary` | `#59636A` (≈ **3,0:1**, **no cumple** WCAG AA para texto pequeño) | `#78838B` (≈ 4,7:1) | `C.TextFaint` |
| `accent` · `accent/hi` · `accent/deep` | `#31D67B` · `#4FEA94` · `#139A52` | igual (usar menos) | `C.Accent*` |
| `accent/soft` | 13 % del acento | igual | `B.AccentSoft` |
| `state/warn` · `state/error` | `#F2B544` · `#FF5F5F` | igual | `C.Warn` · `C.Danger` |
| `glass` · `glass/hi` | 5 % · 9 % blanco | igual | `B.Glass*` |
| `radius/card` · `inner` · `row` · `pill` | 18 · 12 · 10 · 50 | igual | estilos `Card`, `InnerCard` |
| `space` | 4 · 8 · 12 · 16 · 20 · 24 · 28 · 32 | escala de 4 | márgenes/rellenos |

**Tipografía:** en la app real, **Segoe UI Variable** (Display para títulos y cifras, Text para el resto) — es de Windows y no está en Pen.dev (que ofrece Google Fonts): usar **Inter** como sustituto en el lienzo, sabiendo que Segoe es algo más estrecha. Escala: 11 versalitas SemiBold · 12 leyenda · 13 cuerpo · 15 sección · 18 subtítulo · 26–28 título · 40 cifra de volumen.

**Iconos:** en la app, **Segoe Fluent Icons** (sin dependencias). En Pen.dev no existe: diseñar con **Lucide** y mantener una tabla de equivalencias (play/stop/pause/volumen/silencio/abrir/ajustes/altavoz/música/advertencia → glifo Segoe). No añadir paquetes de iconos a la app salvo decisión explícita.

**Sombras y efectos:** una sombra suave por tarjeta (como `Fx.Card`) y el brillo verde solo en el botón activo. **Evitar desenfoque de fondo** (no reproducible en las ventanas transparentes de WPF; ver §9).

**Movimiento:** 150–320 ms con salida suave; anillos del botón solo en directo; atenuación del mini 220 ms; HUD visible 1,6 s + 250 ms de fundido. Respetar «Mostrar animaciones» de Windows (hoy no se respeta; añadirlo en la fase 4).

### 7.3 Pantallas y componentes a diseñar

1. **Ventana principal 1280×840** y su versión mínima **1080×720** (hoy la fila inferior de tarjetas se corta a 900 px de alto: diseñar con desplazamiento previsto).
2. **Selección de altavoz y conexión:** lista lateral (en línea / sin conexión / transmitiendo), estado del altavoz en la tarjeta principal, modelo e IP en detalle.
3. **Controles de transmisión:** botón principal (listo / conectando / en directo / reconectando / deshabilitado), pausa, tiempo, temporizador con cuenta atrás.
4. **Fuente de audio:** segmentado, dispositivo, lista de apps (icono, nombre, sonando/en silencio, seleccionada), YouTube Music aislado, **indicador de aislamiento** (§4.3), ahora suena.
5. **Volúmenes:** volumen del Sonos (principal); **salida local** solo como «Silenciar el PC» (y, si se decide, un volumen de salida etiquetado «afecta a todo lo que suena en *dispositivo*»). Ecualizador del Sonos.
6. **Mini reproductor** 760×100 (+ selector): con y sin título; transmitiendo / en pausa / listo / sin altavoz; atenuado.
7. **HUD:** volumen con barra, silencio, pausa, reanudado, «Sonos sin conexión».
8. **Ajustes:** General, Mini reproductor y atajos (incluido el aviso de conflicto), Audio (margen anti-cortes con explicación), Diagnóstico, Avanzado (copias de seguridad), Acerca de.
9. **Estados:** buscando altavoces · ningún Sonos · esperando a la red y al Sonos (intento n) · conectando · en directo · en pausa · reconectando · detenido por el Sonos (pausa/otra fuente) · error (mensaje concreto) · la app se cerró (esperando a que vuelva) · red con atascos (naranja) · controles aún no disponibles.
10. **Vacíos:** sin apps con audio · sin título (mostrar nombre de la fuente, nunca un texto inventado) · sin altavoces.
11. **Accesibilidad:** foco visible en todo lo navegable con teclado (existe `FocusRing`), orden de tabulación, `AutomationProperties.Name` en cada control, contraste AA, objetivos ≥ 32 px. El mini reproductor es deliberadamente no enfocable (se controla con ratón y atajos): documentarlo en el diseño.

---

## 8. Correspondencia diseño → código

| Componente de diseño | Implementación actual | Enlaces / nombres que deben conservarse |
|---|---|---|
| Tokens | `Theme/Theme.xaml` (`C.*`, `B.*`, `F.*`, `T.*`) | Claves usadas por las tres ventanas y el HUD |
| Card / InnerCard | estilos `Card`, `InnerCard` | — |
| Botón principal | `MainWindow.xaml` `StreamButton` + `Ring1/Ring1S/Ring2/Ring2S` | `ToggleStreamCommand`; los anillos los anima `MainWindow.xaml.cs` por nombre |
| Navegación | `NavHome`, `NavSettings` (estilo `Nav`), `HomePage`, `SettingsPage` | nombres usados en `ShowPage()` |
| Fila de altavoz | plantilla en `MainWindow.Resources` | `SelectSpeakerCommand`, `IsSelected`, `IsOnline`, `IsStreaming` |
| Ficha de estadística | plantilla en `MainWindow.Resources` | `*Text`, `NetworkWarning` |
| Interruptor / deslizador / combo / segmentado | `Switch`, `Slider.Flow`, `Combo.Flow`, `Seg` | — |
| Fila de app | `AppRowButton`, `ActiveDot` | `SelectAppCommand` (vía `DataContext` de la ventana) |
| Espectro | `Controls/SpectrumVisualizer.cs` (`x:Name="Viz"`) | `Analyzer`; `PeakDbfs` por `ElementName=Viz` |
| Anillo de nivel | `Controls/LevelRing.cs` (mini) | `Level` fijado desde el código del mini |
| Mini reproductor | `MiniPlayerWindow` (clase y estilo sin foco) | `SelfTest` 1b comprueba la clase y `WS_EX_NOACTIVATE`; `App` usa `ShowAtSavedPosition`, `SaveScreenshot`, `CloseForShutdown` |
| HUD | `VolumeHudWindow.Flash(glifo, texto, fracción)` | `HudRequested` |
| Bandeja | `TrayIcon` (WinForms) | — |
| Captura de pantalla | `MainWindow.SaveScreenshot` | modos `--screenshot*` |

---

## 9. Integración con Pen.dev (fase 4) — **[verificado]**

**Estado real en este PC (28/10/2026):**
- App **Pen 1.2.20** instalada en `%LOCALAPPDATA%\Programs\Pen`.
- Servidor MCP **`pencil`** registrado en Claude Code (ámbito de usuario, `~/.claude.json`, transporte stdio: `…\Pen\resources\app.asar.unpacked\out\mcp-server-windows-x64.exe --app desktop --agent claudeCodeCLI`) y permitido en `~/.claude/settings.json`.
- Con la app cerrada, todas las llamadas fallan («transport not connected to app: desktop»). **Con Pen abierto funciona:** `get_app_state` respondió (sesión de pen.dev iniciada, un documento vacío `untitled.pen` en `C:\Users\Admin\.pencil\documents\…`), y `read_skill` devolvió la guía oficial.
- No se ha creado ni modificado ningún diseño.

**Qué permite (según la guía de la propia herramienta, sin inventar):**
- `execute`: crear y editar el lienzo con un API JavaScript (`Insert`, `Update`, `Replace`, `Get`, `Print`, `TakeScreenshot`), variables (tokens), componentes reutilizables e instancias, iconos Lucide/Feather/Material Symbols, todas las Google Fonts.
- `get_style`: estilos de partida (no necesarios: Audiora ya tiene identidad).
- `browser`: cargar páginas `http(s)` o `file://` y llevarlas al lienzo como capas o captura.
- Generación de código: la guía está orientada a **frameworks web (React + Tailwind)**. **No hay exportación a WPF/XAML.**

**Flujo propuesto:**
1. Abrir Pen y guardar el documento como `design/audiora.pen` (en el repositorio o fuera; ver bloqueo 4).
2. Claude Code crea en el lienzo los tokens de §7.2 como variables y los componentes de §8 como reutilizables; luego las pantallas y estados de §7.3.
3. Como referencia, generar capturas de la interfaz actual (`SonosFlow.exe --screenshot … --page home|settings`, `--screenshot-mini`, `--screenshot-hud`) y llevarlas al lienzo con `browser` + `file://` (**no probado todavía**).
4. Iterar con el usuario directamente en Pen (es colaborativo y en tiempo real).
5. Implementación: Claude Code **lee** los nodos (`Get`/`Print`) y traduce a mano a `Theme.xaml`/XAML respetando §8; validación con `--screenshot` comparando con `TakeScreenshot` del diseño.

**Bloqueos y limitaciones (ninguno impide empezar):**
1. **Pen debe estar abierto** durante el trabajo de diseño (el MCP solo hace de puente).
2. **Sin exportador a XAML:** la traducción diseño → WPF es manual (asistida por Claude Code). Hay que diseñar con las capacidades de WPF en mente.
3. **Fuentes e iconos:** Segoe UI Variable y Segoe Fluent Icons no existen en Pen → Inter y Lucide como sustitutos con tabla de equivalencias.
4. **Los `.pen` están cifrados** y solo se leen por MCP: no se pueden revisar en un diff de Git. Si se guardan en el repo, tratarlos como binarios y acompañarlos de capturas exportadas.
5. **Efectos no trasladables:** desenfoque de fondo/acrílico en el mini y el HUD (ventanas por capas de WPF); sombras y brillos numerosos (coste de renderizado por software). Diseñar con transparencias planas.
6. El documento puede cambiar mientras se trabaja (multiusuario): releer antes de editar.

---

## 10. Plan de implementación seguro

Cada fase en su rama, con copia de seguridad previa (`sfbackup create`), y sin tocar `SonosFlow.Core`.

| Fase | Contenido | Cambia comportamiento | Verificación |
|---|---|---|---|
| 0 | Diseño en Pen.dev: tokens, componentes, pantallas y estados de §7 | No | Revisión con el usuario |
| 1 | Valores de tokens en `Theme.xaml` (incl. `text/tertiary` accesible) | No | Build, 42 pruebas, capturas antes/después |
| 2 | Llevar estilos locales y colores literales (mini, HUD, fichas, filas) al tema; HUD a XAML | No | Igual + `--screenshot-mini`, `--screenshot-hud` |
| 3 | Dividir `MainWindow.xaml` en `UserControl`s por tarjeta/sección con el mismo `DataContext` | No | Igual + comprobación de enlaces (§11) |
| 4 | Nueva disposición según Pen: jerarquía, estados visuales, vacíos, `UiPhase` derivado, animaciones con preferencia del sistema | Solo presentación | Igual + autotest con el Sonos |
| 5 | Mejoras de UX que necesitan el ViewModel: indicador de aislamiento y guía de salida por app (§4.3); opcional volumen de salida local etiquetado | Sí (interfaz; motor no) | Igual + revisión independiente |
| 6 | Mini reproductor rediseñado | Solo presentación | `SelfTest` 1b–1d, prueba de foco con un juego |

Riesgos a vigilar: perder un enlace silenciosamente (WPF no falla al compilar por un `Binding` mal escrito → revisar la salida de depuración y §11), romper los nombres que usa el código (`Ring1`, `NavHome`, `Viz`…), y el coste de efectos en ventanas transparentes.

---

## 11. Requisitos de compilación y regresión

Antes de integrar cualquier fase:
1. `dotnet build SonosFlow.sln -c Release` → 0 avisos, 0 errores.
2. `dotnet test SonosFlow.sln -c Release` → 42/42 (o más).
3. Capturas comparadas con la referencia: `SonosFlow.exe --screenshot <png> --page home --size 1280x840 --delay 4000`, `--page settings`, tamaño mínimo `--size 1080x720`, `--screenshot-mini`, `--screenshot-mini --expand`, `--screenshot-hud`.
4. **Ningún error de enlace:** ejecutar con el depurador o un `PresentationTraceSources` y revisar que no aparecen `BindingExpression path error`.
5. Si se toca algo enlazado al streaming o al mini: autotest con el Sonos (`--selftest-session`, 26/26) con el puerto 8780 libre, Audiora cerrado y nada sonando en el PC; después, ninguna salida silenciada.
6. Comprobación manual de foco: el mini y el HUD no deben quitar el foco a un juego en ventana sin bordes.
7. Navegación completa con teclado en la ventana principal (Tab, Espacio, flechas en deslizadores y combos).
8. `settings.json` existente se carga sin pérdida.
9. Revisión independiente (`audiora-code-reviewer`) para las fases 3–6.

---

## 12. Recomendación y próximos pasos

1. **Empezar por la fase 0 en Pen.dev**: con Pen abierto, pedir a Claude Code que cree el documento `audiora.pen` con los tokens de §7.2, la biblioteca de componentes y la pantalla principal en sus estados clave (listo, en directo, reconectando, error). Revisarlo juntos antes de tocar código.
2. Decidir dónde se guarda el `.pen` (repositorio como binario o fuera) y si se añade el **indicador de aislamiento** (fase 5), que es la mejora de experiencia con más valor según §4.
3. Fases 1–3 (sin cambios de comportamiento) pueden hacerse después con riesgo bajo, en ese orden.
