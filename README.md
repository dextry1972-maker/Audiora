# Audiora

[![CI](https://github.com/dextry1972-maker/Audiora/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/dextry1972-maker/Audiora/actions/workflows/ci.yml)

Audiora streams audio from your PC or a selected app to a compatible Sonos device. Download the installer, or extract the ZIP and open `Audiora\SonosFlow.exe`. No separate .NET installation is needed. This unsigned beta may trigger a Windows security warning. Please report bugs in the repository’s Issues tab.

*(Formerly “Sonos Flow”. The technical names — the `SonosFlow.*` projects, `SonosFlow.exe`, and the data and backup folders — are unchanged.)*

A Windows desktop app that **streams your PC’s audio to a Sonos speaker over Wi-Fi**, without the official Sonos app, Bluetooth or cables.

How it works: Audiora captures the system audio with WASAPI loopback, serves it as a WAV stream on the local network and tells the Sonos (UPnP, port 1400) to play it. The Sonos then downloads the stream. Details and measurements (in Spanish): [docs/INFORME-TECNICO.md](docs/INFORME-TECNICO.md).

## Install

1. Download `Audiora-Beta-Setup-win-x64.exe` from the [Releases](https://github.com/dextry1972-maker/Audiora/releases) page. If you build it yourself, it is `artifacts\SonosFlow-Setup.exe`.
2. Click **Install**. Audiora installs for your user only in `%LOCALAPPDATA%\Programs\SonosFlow` (no administrator rights needed) and creates shortcuts on the Desktop and in the Start menu.
3. The first time you stream, Windows Firewall may ask for permission. Allow access **on private networks only**: the Sonos needs it to download the audio from the PC.

Because the app is not digitally signed, SmartScreen may show “Windows protected your PC”. Choose *More info → Run anyway*.

**Uninstall:** Settings → Apps → Installed apps → Audiora → Uninstall. Your settings (`%APPDATA%\SonosFlow`) and logs (`%LOCALAPPDATA%\SonosFlow`) are kept.

## Use

- The Sonos is detected automatically. If you have several, choose one under **Speakers on the network**.
- Press **Stream to …** to start streaming, and press it again to stop.
- **Mute PC** (on by default): only the Sonos plays. The PC’s speakers are restored when you stop, when you quit, and even after an unexpected shutdown.
- **Full level:** Windows applies its volume to the captured audio. In **Whole PC** mode, this option raises the Windows volume to 100% while the PC is muted, so you control the volume from the Sonos.
- The Sonos **volume, bass, treble and loudness** are controlled from the app (the speaker applies them).
- When you close the window, the app keeps running in the system tray (you can change this in Settings).

## Build

Requires the .NET 8 SDK.

```powershell
dotnet build SonosFlow.sln -c Release      # build
.\build.ps1                                 # self-contained app + installer in artifacts\
```

**Continuous integration:** GitHub Actions builds and tests every push to `main` and every pull request on Windows,
then builds the installer with `build.ps1`. The installer and the portable app can be downloaded from each run for 14
days. How to read the results and what it does not cover: [docs/CI.md](docs/CI.md).

## Project layout

| Folder | Contents |
|---|---|
| `src/SonosFlow.Core` | Capture (`Audio/`), HTTP server (`Net/`), Sonos control and discovery (`Sonos/`), streaming session with reconnection (`Streaming/`) |
| `src/SonosFlow.App` | WPF interface: theme, spectrum visualizer, tray, settings, uninstall |
| `src/SonosFlow.Setup` | Per-user installer (.NET Framework 4.8, included in Windows) |
| `src/SonosFlow.Backup` | Backup and restore engine (no dependencies) |
| `src/SonosFlow.BackupCli` + `sfbackup.cmd` | Command-line backup tool |
| `tests/` | Automated tests (xUnit) |
| `tools/SonosFlow.CaptureCheck` | Automated check that per-app capture is isolated (test tones on an output nobody hears) |
| `poc/` | Minimal console prototype used to validate streaming and take measurements |
| `tools/make_icon.py` | Generates the icon (no dependencies) |

## Streaming a single app

In **One app** mode you see the apps that have audio (icon, name and whether they are playing; the list updates by itself). When you choose one, **only its audio** goes to the Sonos; games, Discord and everything else keep playing on their usual outputs. You can change the source while streaming without reconnecting the Sonos, and go back to **Whole PC** at any time.

- Uses Windows per-process capture (Windows 10 version 2004 or later). It installs no drivers and does not change the default output.
- **Mute PC in this mode:** only that app goes quiet on the PC. Windows sends it to an output nobody hears, while other apps and games keep their sound and the Sonos still receives it. If the PC has no such output, the app’s output is muted instead. Everything is restored when you stop, when you quit, and even after an unexpected shutdown.
- Per-app capture does not depend on the Windows volume or mute state. In this mode Audiora’s volume is the Sonos volume, and it never changes the Windows master volume.
- What **does not** work is muting or lowering **the app itself** in the Windows volume mixer: that silence reaches the capture.
- **Full level** applies only in **Whole PC** mode, where the capture depends on the Windows volume.
- **Browsers:** Chrome, Edge and Brave play the audio of **all their tabs** from a single process, so you can’t send just one tab. For YouTube Music, use **Open isolated YouTube Music**: it opens music.youtube.com in a window with its own profile (Brave if installed; otherwise Edge or Chrome). That window is a separate process and appears as “YouTube Music (isolated instance)”. The first time, you need to sign in in that window.
- If the app closes, streaming continues (in silence). When the app is opened again, Audiora picks it up automatically and gives the Sonos a fresh connection, without changing your volume or settings.
- Windows system sounds can’t be captured separately.

## Dropout buffer

The Sonos plays live, so a short Wi-Fi pause can be heard as a dropout. With **Settings → Audio → Dropout buffer** (1 s by default), the Sonos receives that much audio up front when it connects and plays with that delay, riding out shorter network pauses. You can choose a preset (no buffer, 100 ms, 300 ms or 1 s) or drag the slider from 0 to 1000 ms in 10 ms steps; the value shown follows the slider as you drag. “No buffer” gives the lowest latency. The buffer applies when streaming starts. To diagnose dropouts, `stream-*.csv` records every second the audio waiting for the Sonos to collect it (`backlog_ms`) and every Sonos connection. Details (in Spanish): [docs/INVESTIGACION-CORTES-6MIN.md](docs/INVESTIGACION-CORTES-6MIN.md).

## Keyboard shortcuts

They work anywhere in Windows, including inside a game, and never take the focus (Settings → Mini player → **Global keyboard shortcuts**, on by default):

| Shortcut | Action |
|---|---|
| Ctrl+Alt+↑ / Ctrl+Alt+↓ | Sonos volume ±5 |
| Ctrl+Alt+P | Pause / resume (or start streaming) |
| Ctrl+Alt+O | Show / hide the mini player |

The keyboard’s media keys are left alone: they keep controlling whatever player is playing. If another program already uses a shortcut, Settings tells you.

## Network indicator

The network indicator in the statistics shows “Stable”, or the number of stalls (seconds in which the Wi-Fi or the Sonos did not collect the audio in time) and the worst one. It turns orange if any stall was longer than the dropout buffer (a likely audible dropout). Each stall is logged with its time in `app-events.log` and in `stream-*.csv`. Very short stalls are absorbed by Windows and are not shown.

## Language

On the first start of a new installation, Audiora asks for your language and suggests the Windows one. You can change it at any time in Settings (**Display language**): the change is immediate and does not interrupt streaming. It includes 12 languages, including Arabic (right to left). All translations except Spanish were machine-made and still need review by native speakers. Architecture and how to add a language: [docs/LOCALIZATION.md](docs/LOCALIZATION.md).

## Mini player

A floating capsule with the source, the real status, the Sonos, the volume (the same as in the main window), **pause** (the Sonos stays connected and receives silence, so resuming is instant), and buttons to open the main window, choose the source and hide it. It opens from the title-bar button, from the tray, or when you minimize or close the window (depending on Settings). You can drag it; it remembers its position, stays on screen, can stay always on top and **never takes the keyboard focus** (it does not interrupt games). Hiding it or switching windows never stops or restarts the audio.

## Backups

Time-stamped backups of the source code, verified with SHA-256, in `%USERPROFILE%\SonosFlow-Backups` (outside the project and outside OneDrive). From the app’s Settings or from the terminal:

```bat
sfbackup create "reason"     & rem new backup
sfbackup list                & rem list backups
sfbackup verify <id>         & rem check integrity
sfbackup plan <id>           & rem what a restore would change (changes nothing)
sfbackup restore <id>        & rem restore after confirmation (backs up the current state first)
```

Full guide (in Spanish): [docs/COPIAS-DE-SEGURIDAD.md](docs/COPIAS-DE-SEGURIDAD.md).

## Privacy and security

- Everything is local: the audio never leaves your network and no data is collected.
- The server listens only on the local network IP and **rejects any connection that does not come from a detected Sonos**.
- No administrator rights. It only writes to your user profile and to `HKCU` (start with Windows and the uninstall entry).
- It does not change the firewall or the antivirus.

## Known limitations

- **Latency:** the Sonos always plays somewhat behind the PC. With the default settings, most of that delay is the **dropout buffer** (Settings → Audio), which you can set anywhere from 0 to 1000 ms in 10 ms steps (1 s by default). A smaller buffer reduces the delay Audiora adds on purpose, but makes audible dropouts more likely when the Wi-Fi pauses. Even at 0 ms the delay is not zero: capture and processing, the network and the Sonos’s own playback buffer still add some. The only figures available are estimates from the playback position the Sonos reports, with about ±1 s uncertainty: roughly 1.1–1.4 s with the default 1 s buffer and roughly 0.5 s with no buffer. The end-to-end delay has not been measured with a microphone (the attempts so far picked up no sound). Games and lip-synced video have not been tested at a low buffer, so try the setting and judge for yourself.
- A single browser tab can’t be captured on its own (see “Streaming a single app”).
- Sonos groups: choose the group’s main speaker.
- Licenses: NAudio (MIT). Personal project, not affiliated with or endorsed by Sonos, Inc.
