# Audiora Aurora 2.0 — rediseño visual

Rama `ui/aurora-2` (desde `main` 0f94e50). **Solo presentación**: el motor de audio, el servidor HTTP, el marcapasos, los búferes, la recuperación y la comunicación con Sonos no se tocan (`git diff main -- src/SonosFlow.Core` vacío).

Prototipos aprobados: lienzo «Audiora Aurora 2.0 — Prototipos» (Inicio, Ajustes, bienvenida, sistema de diseño, estados, comparativa).

## Qué cambia

| Área | Antes | Ahora |
|---|---|---|
| Paleta | casi negro + verde esmeralda | azul noche `#0B1026`, cobalto (cristal), violeta `#8957F5`, cian `#37DDF3` («en directo»), lavanda `#B6A5FF` |
| Tokens | `Theme.xaml` | mismas claves (`C.*`, `B.*`) con valores nuevos; nuevas `C.Accent2/B.Accent2` (cian), `B.AccentDeep`, `B.ToggleOn`, `Img.Logo` |
| Material | paneles translúcidos verdosos | cristal cobalto con borde lavanda→cian (`B.Panel`, `B.PanelEdge`) |
| Fondo | aurora verde | luces violeta (arriba a la derecha) y cian (abajo a la izquierda), sin animación en reposo |
| Logo | PNG verde | vector de cinco barras (`Img.Logo`, `DrawingImage`), nítido a cualquier DPI |
| Botón principal / controles | verde | degradado cian→azul→violeta; icono y texto sobre el degradado en navy `#071033` (contraste 6–11:1) |
| Ajustes | una columna larga | seis pestañas: Personalización, General, Audio, Sonidos, Mini reproductor, Avanzado (`Tab.Pill`) |
| Personalización | — | nombre para mostrar + «Bienvenida personalizada» (`Input.Flow`) |
| Bienvenida | solo idioma | idioma → «¿Cómo quieres que te llamemos?» (opcional) y, en los inicios siguientes, «Te damos la bienvenida de nuevo, <nombre>.» |

## Lo que se conserva
Todas las funciones y textos; las 12 traducciones (más 18 claves nuevas por idioma) y el árabe (RTL); el movimiento reducido (`Motion.Enabled`); el interruptor con su pieza `Knob` y los desplazamientos de ±8 que comprueba `MotionTests`; un único sistema de sonidos de interfaz.

## Archivos
- `Theme/Theme.xaml`: tokens, plantillas con colores propios (`Switch`, `Slider.*`, `Combo.Flow`, `Seg`, `ChipButton`, `Flyout`…), `Img.Logo`, `Input.Flow`, `Tab.Pill`.
- `Controls/AuroraBackground.xaml`, `LevelRing.cs`, `RadialSpectrum.cs`, `SpectrumVisualizer.cs`: colores fijos migrados.
- `MainWindow.xaml`, `MiniPlayerWindow.xaml`, `VolumeHudWindow.cs`, `Views/LanguageWindow.xaml`: verdes fijos y logo.
- `Views/SettingsView.xaml(.cs)`: pestañas y tarjeta de personalización.
- `Views/NameWindow.*`, `Views/WelcomeBackWindow.*`, `Infrastructure/WindowCapture.cs`, `App.xaml.cs`: bienvenida.
- `Infrastructure/AppSettings.cs`, `ViewModels/MainViewModel.Profile.cs`: `UserName`, `PersonalizedWelcome`, `NameOnboardingDone`.
- `Localization/Strings/*/{settings,main,onboarding}.json`: 18 claves × 12 idiomas.
- Pruebas: `ProfileTests`.

## Privacidad
El nombre se guarda solo en `%APPDATA%\SonosFlow\settings.json`; no se escribe en `app-events.log` ni en el diagnóstico. En árabe se aísla con marcas bidi dentro de la frase.

## Comportamiento de la bienvenida
- Primera ejecución (no existía el archivo de ajustes): idioma → nombre (se puede omitir).
- Instalaciones existentes: no se les interrumpe con una pregunta; el saludo aparece cuando ponen el nombre en Ajustes.
- El saludo de regreso (≈1,7 s; ≈1,1 s con las animaciones de Windows desactivadas; se cierra con un clic o una tecla) **no** se muestra en inicios automáticos (`--minimized`, reanudar transmisión), en capturas ni en modos de prueba.

## Capturas de comprobación
`SonosFlow.exe --page home|settings --mode system --screenshot <png> --size 1100x760 --lang <código>`, `--screenshot-mini`, `--screenshot-hud`, `--screenshot-welcome`, `--screenshot-name <png> [--name Texto]`, `--screenshot-back <png> [--name Texto]`. Nunca con `--minimized`.

## Pendiente / límites conocidos
- Icono de la aplicación y de la bandeja (`Assets/SonosFlow.ico`) y el instalador siguen en verde: requieren regenerar el `.ico` (decisión aparte).
- Alto contraste de Windows: el tema no reacciona (como antes); falta un modo de paneles opacos.
- No verificado a 125 %/150 % de DPI ni con lector de pantalla; las traducciones nuevas necesitan revisión de hablantes nativos.
- Cristal esmerilado «real» (desenfoque del fondo) no incluido: WPF no lo ofrece sin coste; se usa cristal ligero.

## Rollback
- Respaldo: etiqueta `backup/pre-aurora-20261103` y copia `C:\Users\Admin\SonosFlow-Backups\SonosFlow_2026-11-03_01-16-14`.
- La rama no se ha fusionado: volver es `git checkout main`. Si se fusiona y se quiere deshacer: `git revert -m 1 <commit de la fusión>`.
