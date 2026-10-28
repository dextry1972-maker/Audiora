# Rediseño de la interfaz — Fase 0 (Pen.dev)

Fecha: 28/10/2026. Diseño hecho en Pen.dev (app Pen 1.2.20) mediante su servidor MCP, sin tocar el código de Audiora. Base: `docs/UI_REDESIGN_AUDIT.md`.
Láminas exportadas (revisables en Git, los `.pen` están cifrados): `docs/design/*.png`.

## 1. Documento e identificadores

**Documento Pen:** `C:\Users\Admin\.pencil\documents\5036105c-9d25-4dec-9fb0-b31ba1351728\untitled.pen` (documento activo de Pen; **pendiente**: guardarlo con un nombre definitivo, ver §7).

| Lámina | ID de nodo | PNG |
|---|---|---|
| Fundamentos (tokens) | `J4Mw7` | `docs/design/00-fundamentos.png` |
| Componentes | `zGH8x` | `docs/design/01-componentes.png` |
| Inicio — En directo (pantalla principal) | `A3yQt` | `docs/design/10-inicio-en-directo.png` |
| Inicio — Listo (detenido desde la app de Sonos) | `bfIeb` | `docs/design/11-inicio-listo-detenido-desde-sonos.png` |
| Inicio — Reconectando | `j1X8WE` | `docs/design/12-inicio-reconectando.png` |
| Inicio — Sin conexión (error) | `OecYr` | `docs/design/13-inicio-sin-conexion.png` |

**Componentes reutilizables (16):** `l6Lof` Botón/Primario · `Jl8f9` Botón/Secundario · `hjdXN` Botón/Texto · `XXZkB` Botón/Icono · `RkjcJ` Interruptor/Activado · `ghwsh` Interruptor/Desactivado · `A3cch6` Estado/Píldora · `rxLVq` Transmisión/Botón · `cHnnB` Deslizador · `M5Q6nq` Segmentado · `aB24V` Fila/Altavoz · `qG4aA` Fila/Aplicación · `i7X31I` Tarjeta (con hueco «Contenido») · `A7zCY` Aviso · `Dbvb5` Dato · `NpDkl` Fila/Ajuste.

## 2. Sistema de diseño

**Color** (variables del documento; mismos valores que `Theme.xaml` salvo lo indicado):

| Variable | Valor | Uso | Recurso WPF |
|---|---|---|---|
| `bg-base` / `bg-raised` | `#07090B` / `#0C1013` | Fondo / barra lateral | `C.Bg0` / `C.Bg1` |
| `surface` / `surface-hi` | `#11161A` / `#161C21` | Tarjetas / elemento elegido | `C.Card` / `C.CardHi` |
| `surface-press` | `#1B2228` | **Nuevo**: segmento activo, fondo deshabilitado | añadir `C.CardPress` |
| `stroke` / `stroke-strong` | `#1E262C` / `#2A343B` | Bordes | `C.Stroke` / `C.StrokeHi` |
| `text-primary` / `text-secondary` | `#F1F4F6` / `#8B959D` | Texto | `C.Text` / `C.TextDim` |
| `text-tertiary` | **`#78838B`** (antes `#59636A`, 3:1 → 4,7:1) | Rótulos, deshabilitado | `C.TextFaint` (cambiar valor) |
| `text-on-accent` | `#04140A` | Texto sobre verde | hoy literal en el botón → añadir `B.OnAccent` |
| `accent` / `accent-hi` / `accent-deep` | `#31D67B` / `#4FEA94` / `#139A52` | Normal / hover / presionado | `C.Accent*` |
| `accent-soft` | `#31D67B22` | Fila elegida, anillo en reposo | `B.AccentSoft` |
| `warn` / `warn-soft` | `#F2B544` / 12 % | Reconectando, atascos | `C.Warn` / añadir `B.WarnSoft` |
| `danger` / `danger-soft` | `#FF5F5F` / 12 % | Error | `C.Danger` / añadir `B.DangerSoft` |
| `glass` / `glass-hi` | blanco 5 % / 9 % | Superficies interiores | `B.Glass` / `B.GlassHi` |

**Regla del verde:** solo para «en directo/activo» y la acción principal. Ámbar = recuperándose; rojo = fallo con acción.
**Tipografía:** Inter en el lienzo ↔ Segoe UI Variable en la app. Escala: 40 cifra · 26–28 título · 18 subtítulo · 15 sección · 13 cuerpo · 12 secundario · 11/10 versalitas.
**Espaciado:** escala de 4 (4–32). Tarjetas con relleno 20 (principal 22×28), separación entre tarjetas 20, contenido a 28 del borde.
**Radios:** tarjeta 18 · interior 12 · fila 10 · píldora. **Sombra:** una por tarjeta (y 6, desenfoque 28, negro 45 %); brillo verde solo en el botón activo. Sin desenfoque de fondo.
**Estados de control** (lámina Componentes): normal, hover (`accent-hi`), presionado (`accent-deep`), deshabilitado (`surface-press` + texto terciario), cargando (icono giratorio + «Conectando…»), error (`danger-soft` + «Reintentar»); interruptores activado/desactivado/bloqueado; botón de transmisión listo/en directo/conectando/reconectando/deshabilitado; píldoras en directo/conectando/reconectando/en pausa/sin conexión/error.

## 3. Estructura de la pantalla principal (1280×840)

```
Inicio — En directo (A3yQt)
├─ Barra lateral (248)
│  ├─ Marca (logo + «Audiora» + lema)
│  ├─ Navegación: Inicio (activo) · Ajustes
│  ├─ ALTAVOCES: Fila/Altavoz «Bedroom · Sonos Five · Transmitiendo»
│  └─ Red local: «Zona Wi-Fi del PC · 1 altavoz · 192.168.137.1» + [Buscar altavoces]
└─ Contenido
   ├─ Barra de título: [mini reproductor] · minimizar · maximizar · cerrar
   ├─ (Aviso — solo en estados Listo-tras-Sonos y Sin conexión)
   ├─ Tarjeta principal
   │  ├─ Estado: píldora · título · «fuente → altavoz» · Ahora suena (título/artista) · tiempo · temporizador
   │  ├─ Control: Transmisión/Botón + «Transmitir/Detener»
   │  └─ Volumen y salida: VOLUMEN DEL SONOS 38 % + deslizador · Silenciar el PC · Nivel completo (bloqueado en modo app) · «Se aplican al iniciar»
   └─ Fila inferior
      ├─ Tarjeta «Fuente de audio»: segmentado + [Abrir YouTube Music aislado] · lista de apps · [MEJORA FUTURA] aviso de aislamiento
      └─ Columna derecha
         ├─ Tarjeta «Sonido del Sonos»: graves · agudos · loudness · [Restablecer]
         └─ Tarjeta «Conexión»: RED · BÚFER · ENVÍO · RECONEXIONES · detalle · [Registros]
```

**Variantes de estado** (copias con cambios mínimos): En directo · Listo tras «Se pausó desde la app de Sonos» (aviso neutro + «Volver a transmitir») · Reconectando (ámbar, «intento 2 de 8», atascos) · Sin conexión (aviso rojo + «Buscar de nuevo», altavoz gris, volumen y sonido inactivos porque `ControlsReady` es falso).

## 4. Qué cambia respecto a la interfaz actual (decisiones a confirmar)

| Cambio | Motivo | Funcionalidad |
|---|---|---|
| Ilustración del altavoz → estado del altavoz en la barra lateral | La acción y el estado son el foco | Se conserva (nombre, modelo, en línea) |
| **Tarjeta «Audio en tiempo real» (espectro) eliminada de Inicio** | Menos ruido visual; el anillo de nivel del mini ya da realimentación | **A decidir**: mantener en un panel «Diagnóstico» plegable o en Ajustes |
| Estadísticas (6 cifras) → tarjeta «Conexión» con 4 | Lo útil para el usuario: red, búfer, envío, reconexiones | Formato y URL del stream pasan al detalle/Ajustes |
| «Último error» → aviso rojo con acción | Error visible y accionable | Mismo texto (`LastErrorText`) |
| «Ahora suena» sube a la tarjeta principal | Es el contenido que el usuario reconoce | `NowPlaying*` |
| Temporizador como chip junto al tiempo | Menos peso visual | `SleepChoices/SleepKey` |
| «Abrir YouTube Music aislado» junto al selector de modo | Ahorra una fila | `LaunchYouTubeMusicCommand` |
| Aviso de aislamiento | **Mejora futura**, etiquetada como tal | Requiere fase 5 (§4 de la auditoría) |

No se añade ninguna función que Audiora no tenga, salvo el aviso de aislamiento, marcado «MEJORA FUTURA».

## 5. Correspondencia propuesta con el código

| Diseño | WPF actual | Propuesta de implementación | Enlaces (sin cambios) |
|---|---|---|---|
| Variables | `Theme.xaml` `C.*`/`B.*` | Ajustar `C.TextFaint`; añadir `C.CardPress`, `B.OnAccent`, `B.WarnSoft`, `B.DangerSoft` | — |
| Botón/Primario, Secundario, Texto, Icono | `Btn.Accent`, `Btn.Ghost`, `Btn.Caption` | Plantillas con estados `IsMouseOver/IsPressed/IsEnabled` = lámina | comandos existentes |
| Interruptor | `Switch` | Retocar plantilla (44×24, pomo 18) | `MuteLocal`, `FullLevel`, `Loudness`… |
| Deslizador | `Slider.Flow` | Pista 4, tirador 16 con sombra | `Volume`, `Bass`, `Treble` |
| Segmentado | `Seg` | Píldora con segmento `surface-press` | `IsAppMode`, `UseSystemCommand`, `UseAppModeCommand` |
| Estado/Píldora | nuevo | Estilo + `DataTrigger` sobre `State`/`IsPaused` (o `UiPhase`, auditoría §6.4) | `State`, `IsPaused`, `StatusTitle` |
| Transmisión/Botón | `StreamButton` + `Ring1/Ring2` | Misma estructura; variantes por `DataTrigger` | `ToggleStreamCommand`, `IsStreaming`, `IsActive`, `State` |
| Fila/Altavoz | plantilla en `MainWindow.Resources` | Mover al tema | `SelectSpeakerCommand`, `IsOnline`, `IsStreaming` |
| Fila/Aplicación | `AppRowButton` | Añadir estado elegido (borde y marca) | `SelectAppCommand`, `IsSelected`, `IsActive` |
| Tarjeta | `Card` | Igual (radio 18, relleno 20) | — |
| Aviso | nuevo | `UserControl` o estilo con `DataTrigger` sobre `State == Error` / mensaje `Released` | `StatusDetail`, `LastErrorText`, `RescanCommand`, `ToggleStreamCommand` |
| Dato | ficha en `MainWindow.Resources` | Estilo `StatTile` en el tema | `NetworkText`, `BufferText`, `RateText`, `ReconnectsText` |
| Fila/Ajuste | filas de Ajustes | Estilo reutilizable | ajustes existentes |
| Tarjeta principal / Fuente / Sonido / Conexión | bloques de `MainWindow.xaml` | `UserControl`s `HeroCard`, `SourceCard`, `SonosSoundCard`, `ConnectionCard` (fase 3), mismo `DataContext` | ver auditoría §6.2 |

Nombres que deben conservarse al implementar: `NavHome`, `NavSettings`, `HomePage`, `SettingsPage`, `MaxButton`, `StreamButton`, `Ring1/Ring1S/Ring2/Ring2S`, `Viz` (si el espectro se conserva), `MiniPlayerWindow`.

## 6. Limitaciones del diseño

- **Fuentes e iconos sustitutos:** Inter ≈ Segoe UI Variable (métricas algo distintas; revisar ajustes de texto al implementar). Lucide ↔ Segoe Fluent: house `E80F`, settings `E713`, play `E768`, square/stop `E71A`, pause `E769`, volume `E767`, rotate `E72C`, alert `E7BA`, info `E946`, speaker `E7F5`, wifi `E701`, check `E73E`, external `E8A7`. Comprobar cada glifo al implementar.
- **Estados interactivos** (hover/presionado) están como variantes estáticas; las animaciones (anillos, giro de «Conectando», fundidos) no se representan.
- El **deslizador** es un dibujo estático: cada valor se ajustó a mano.
- Solo se ha diseñado **1280×840**; falta la versión mínima **1080×720** (la fila inferior necesitará desplazamiento o columnas más estrechas).
- **Fuera del alcance de la fase 0:** Ajustes, mini reproductor, HUD, bandeja.
- El hueco de la tarjeta aparece rayado en el componente maestro (marca de Pen para huecos vacíos); en las pantallas se rellena.
- El documento sigue como `untitled.pen` en la carpeta de Pen.

## 7. Siguientes pasos

1. **Revisión del usuario** de las láminas (sobre todo §4: quitar el espectro de Inicio y la ilustración del altavoz).
2. Guardar el documento en Pen con un nombre definitivo («Archivo → Guardar como»), p. ej. `design/audiora.pen` dentro del repositorio (binario) o en la carpeta de Pen; Claude Code no puede renombrarlo por MCP.
3. Completar el diseño: versión 1080×720, Ajustes, mini reproductor (con y sin título), HUD y estados vacíos.
4. Implementación por fases de `docs/UI_REDESIGN_AUDIT.md` §10: **fase 1** (valores de tokens en `Theme.xaml`, incluido el gris terciario accesible) → fase 2 (estilos al tema) → fase 3 (`UserControl`s) → fase 4 (disposición de esta lámina), con capturas `--screenshot` comparadas con `docs/design/*.png` y las pruebas de §11.
