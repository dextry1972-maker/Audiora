# AGENTS.md — instrucciones para agentes en Audiora

Complementa a `CLAUDE.md` (reglas del proyecto, que prevalecen). Nombre visible **Audiora**; identificadores técnicos **SonosFlow** (no renombrar).

## Proyecto en una línea
WPF/.NET 8 que captura el audio del PC (todo el sistema o una aplicación), lo sirve como WAV infinito por HTTP en la red local y ordena al Sonos (UPnP) reproducirlo. Ver `docs/ARCHITECTURE.md`.

## Agentes y responsabilidades
Definiciones en `.claude/agents/`. Cada tarea tiene **un dueño**, archivos en alcance, entregables y criterio de aceptación.

| Agente | Dueño de |
|---|---|
| `audiora-lead-architect` | Descomposición, integración, criterios de aceptación, `docs/ROADMAP.md` |
| `audiora-audio-engineer` | `Core/Audio`, `Core/Streaming`, `Core/Net/WavStreamServer.cs` |
| `audiora-network-engineer` | `Core/Sonos`, descubrimiento, reconexión, tiempos de espera |
| `audiora-windows-engineer` | Ciclo de vida, inicio con Windows, atajos, GSMTC, instalador |
| `audiora-ui-engineer` | XAML, ViewModels, mini reproductor, HUD, temporizador |
| `audiora-qa-engineer` | Pruebas automáticas, simuladas y con el Sonos real |
| `audiora-code-reviewer` | Revisión independiente (solo lectura) antes de integrar |

## Coordinación
- Investigación en paralelo; **un solo escritor por archivo** a la vez. Cambios que tocan archivos compartidos (`MainViewModel*.cs`, `StreamingSession.cs`, `MainWindow.xaml`, `AppSettings.cs`) se serializan a través del arquitecto.
- Trabajo en ramas (`mejoras/…`); nada se fusiona a `main` ni se publica sin autorización del usuario. Nunca `push`.
- Cada agente informa: hallazgos, archivos tocados, pruebas ejecutadas **con su resultado real**, riesgos abiertos.
- Ninguna implementación se da por buena solo con la palabra de su autor: QA + revisor independiente para cambios de riesgo.

## Convenciones
- C# con nullable, `sealed`, comentarios en inglés y escasos; texto de la interfaz y documentación en español.
- Ajustes nuevos en `AppSettings` siempre con valor por defecto (compatibilidad con `settings.json` existentes).
- Core Audio se crea y libera en hilos del pool (MTA); la UI solo en el dispatcher.
- Reintentos acotados con espera creciente; nunca bucles sin límite.

## Calidad (obligatorio antes de integrar)
1. `dotnet build SonosFlow.sln -c Release` sin avisos nuevos.
2. `dotnet test SonosFlow.sln -c Release` en verde; pruebas nuevas para la lógica nueva.
3. Si se toca el streaming: `CaptureCheck soak` y autotest con el Sonos (`docs/TESTING.md`), solo con el puerto 8780 libre y Audiora cerrado.
4. Revisar `app-events.log` / `stream-*.csv` de la prueba; comprobar que ninguna salida queda silenciada.
5. Distinguir siempre: **automática**, **simulada** o **con el Sonos real**. No afirmar verificación de hardware no realizada.

## Prohibido sin autorización
Ver «Nunca» en `CLAUDE.md`. Además: no cambiar volumen/EQ del Sonos, red, zona Wi-Fi ni firewall; no instalar software global; no publicar ni hacer push; no descartar cambios del usuario.
