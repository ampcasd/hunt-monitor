# Tibia Square Hunt Monitor

Windows desktop app that passively tracks Tibia hunt sessions by capturing the game through OBS and reading on-screen data.

The public repository contains the desktop shell: tray UI, OBS orchestration, storage, auth, sync, packaging, public interfaces, and the obfuscated processing binary used by local builds. OCR parsing and session-processing logic is loaded at runtime from `lib/TibiaSquare.HuntMonitor.Processing.dll`.

## Requirements

- Windows 10 1903 or later
- .NET 8 SDK
- Windows 10 SDK, for local MSIX packaging
- OBS Studio, or the official bundled OBS release package

## Build

```powershell
dotnet build TibiaSquare.Desktop.sln
```

The app builds without the processing DLL, but full capture/session tracking needs `lib/TibiaSquare.HuntMonitor.Processing.dll`.

## Tests

```powershell
dotnet test TibiaSquare.Desktop.sln
```

The live diagnostic upload test is skipped unless `RUN_LIVE_DESKTOP_TESTS=1` is set and the local app auth/config files are available.

## Local Configuration

Copy `src/TibiaSquare.HuntMonitor/config.example.json` to `src/TibiaSquare.HuntMonitor/config.json` for local development.

`config.json` is intentionally ignored by git.

## Processing DLL

For normal use, install Hunt Monitor from the Microsoft Store. This public repository does not publish unsigned GitHub release builds.

For local development against the full capture pipeline, the repository includes:

```text
lib/TibiaSquare.HuntMonitor.Processing.dll
```

The loader also checks for the DLL next to the executable. Without the DLL, the app still builds and opens, but capture/session-processing features are disabled.

## Anti-Cheat Safety

The app is designed to be fully passive:

- No game memory access
- No input simulation
- No Tibia client modification
- No gameplay automation

It reads pixels from OBS and stores hunt/session data locally before syncing only through the documented Tibia Square API.
