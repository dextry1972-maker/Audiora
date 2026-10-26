# Sonos Flow — reglas del proyecto

Nombre visible de la app: **Audiora**. Los identificadores técnicos siguen siendo `SonosFlow` (namespaces, `SonosFlow.exe`, `%APPDATA%\SonosFlow`, claves HKCU, mutex, `SonosFlow-Backups`, prefijo `SonosFlow_`, `/sonosflow.wav`): no renombrarlos sin autorización, rompen ajustes, copias y el instalador.

C# / .NET 8 (WPF + biblioteca Core), instalador en .NET Framework 4.8. Compilar: `dotnet build SonosFlow.sln -c Release`. Pruebas: `dotnet test SonosFlow.sln -c Release`. Instalador: pasos de `build.ps1` (no lanzar con `-ExecutionPolicy Bypass`).

## Protocolo antes de cualquier cambio importante

1. Examinar el estado: `git status` y `git log --oneline -5`. No descartar, revertir ni limpiar cambios del usuario.
2. Si hay riesgo significativo (refactor, cambios en `src/SonosFlow.Core/` — audio/red/Sonos —, instalador, restauraciones): crear copia y verificarla:
   `src\SonosFlow.BackupCli\bin\Release\net8.0\SonosFlow.BackupCli.exe create "Antes de <cambio>"` (o `sfbackup create …`).
3. Identificar los componentes afectados y conservar lo que funciona; cambios en pasos pequeños.
4. Ejecutar `dotnet build` y `dotnet test`; si se toca el streaming, prueba real con
   `SonosFlow.exe --autostart --screenshot <png> --delay 15000` y revisar `%LOCALAPPDATA%\SonosFlow\logs\stream-*.csv` y `app-events.log`.
5. Comparar con el estado anterior e informar de cambios y problemas. Si una prueba falla, decirlo: no afirmar que funciona.
6. No revertir automáticamente ni sobrescribir para "arreglar" un fallo: primero la causa; recuperar con `sfbackup plan/restore` (con confirmación del usuario).
7. Commits descriptivos de los cambios importantes, sin secretos.

## Nunca

- Borrar o sobrescribir copias de `C:\Users\Admin\SonosFlow-Backups`.
- Borrar archivos del proyecto sin autorización.
- Crear tareas programadas o servicios sin autorización.
- Cambiar el volumen/ecualizador del Sonos, la red o el firewall para pruebas.
- Dejar el PC silenciado tras una prueba: comprobar `AudioEndpointVolume.Mute` al terminar.

Detalles: `docs/COPIAS-DE-SEGURIDAD.md`, `docs/INFORME-TECNICO.md`.
