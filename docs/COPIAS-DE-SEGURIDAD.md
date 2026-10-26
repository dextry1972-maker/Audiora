# Copias de seguridad y restauración de Sonos Flow

Protege el **código fuente del proyecto** (no la app instalada ni tus ajustes de audio).

## Dónde están

| Qué | Ubicación |
|---|---|
| Copias | `C:\Users\Admin\SonosFlow-Backups\SonosFlow_YYYY-MM-DD_HH-mm-ss\` |
| Registro de operaciones | `C:\Users\Admin\SonosFlow-Backups\backup-log.csv` |
| Copia inicial (antes de este sistema) | `SonosFlow_2026-10-26_03-31-13` (57 archivos) |

La carpeta está **fuera del proyecto y fuera de OneDrive**: no se sincroniza con la nube. Para tener una copia fuera del PC, copia esa carpeta a un disco externo.

Cada copia contiene `files\` (los archivos) y `manifest.json` (fecha, motivo, lista de archivos con tamaño y SHA-256, exclusiones y archivos omitidos por posibles secretos).

## Qué se copia y qué no

- **Sí:** código (`.cs`, `.xaml`, `.csproj`, `.sln`), recursos (icono), manifiestos, scripts, documentación, registros de pruebas de `poc/`, `.gitignore`.
- **No (regenerable):** `bin/`, `obj/`, `artifacts/`, `TestResults/`, `.vs/`, `.vscode/`, `__pycache__/`, `node_modules/`, `*.user`, `*.suo`, `*.pyc`.
- **No (historial):** `.git/` — Git es la segunda red de seguridad, independiente.
- **Nunca (secretos):** `.env*`, `*.pfx`, `*.p12`, `*.pem`, `*.key`, `*.snk`, `secrets.json`, `id_rsa*`, `*.kdbx`, y cualquier archivo de texto que contenga una clave privada o un token (GitHub, OpenAI, AWS, Slack). Se listan en `skippedSecrets` del manifiesto para que sepas qué volver a configurar a mano. Hoy el proyecto **no tiene ningún secreto**.

## Crear una copia manual

Desde la app: **Ajustes → Copias de seguridad → Crear copia ahora**.

Desde una terminal, en la carpeta del proyecto:

```bat
sfbackup create "Antes de cambiar el streaming"
```

Cada copia lleva fecha y hora; si coinciden en el mismo segundo se añade `_2`, `_3`… **Nunca se sobrescribe ni se borra una copia.** La copia se escribe primero en `<id>.partial`, se verifica leyendo de nuevo cada archivo (SHA-256) y solo entonces recibe su nombre definitivo.

## Ver y verificar copias

```bat
sfbackup list
sfbackup verify SonosFlow_2026-10-26_03-31-13
sfbackup log
```

`verify` recalcula el SHA-256 de cada archivo y comprueba que estén los esenciales (`SonosFlow.sln`, `README.md` y los `.csproj` de Core, App y Setup). En la app: selecciona una copia y pulsa **Verificar**.

## Restaurar una versión anterior

1. Mira qué cambiaría (no modifica nada):
   ```bat
   sfbackup plan SonosFlow_2026-10-26_03-31-13
   ```
2. Restaura (pide escribir `RESTAURAR`):
   ```bat
   sfbackup restore SonosFlow_2026-10-26_03-31-13
   ```
   En la app: selecciona la copia → **Restaurar…** → revisa la lista → **Sí**.

Lo que hace la restauración, en orden:

1. Verifica la copia; si está dañada, **no restaura**.
2. Calcula el plan: archivos que se crearán, que se sobrescribirán, sin cambios y **los que no están en la copia (no se tocan nunca)**.
3. Crea automáticamente una copia del estado actual (`Automática antes de restaurar …`).
4. Escribe solo los archivos que cambian (cada uno vía archivo temporal).
5. Comprueba que todos los archivos restaurados coinciden con la copia y que la estructura del proyecto es coherente.
6. Registra el resultado en `backup-log.csv`.

Para **deshacer una restauración**, restaura la copia automática que se creó en el paso 3.

Para probar una copia **sin tocar el proyecto**, restaura en otra carpeta:

```bat
sfbackup restore SonosFlow_2026-10-26_03-31-13 --target C:\Temp\prueba-sonosflow
```

Después de restaurar código hay que **recompilar** (`dotnet build SonosFlow.sln -c Release` o `build.ps1`) para que la app refleje los cambios.

## Sistema automático

No hay ninguna tarea programada ni servicio en segundo plano (requeriría tu autorización). Las copias automáticas solo ocurren **antes de cada restauración**. Las copias antes de cambios importantes las hace Claude siguiendo el protocolo de `CLAUDE.md`, o tú con `sfbackup create`.

## Git

El repositorio local (rama `main`) guarda el historial de cambios. Autor configurado solo para este repositorio (`git config --local`). No hay remoto: nada se sube a Internet.

```bat
git log --oneline
git status
```
