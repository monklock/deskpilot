# DeskPilot

DeskPilot is a local Windows 10/11 voice assistant for deterministic desktop commands.

The current development version implements manual and voice-controlled Windows audio: offline wake word and Russian speech recognition, saved speakers/headphones switching, volume and mute commands, safe WPF feedback, local persistence, model management, and a modular command-dispatch boundary. Application launching and command groups remain planned for Milestone 4.

Supported Russian audio phrases and matching rules are documented in [docs/voice-commands.md](docs/voice-commands.md).

## Requirements

- Windows 10 or Windows 11 x64
- .NET SDK 10.0.300 or newer compatible feature band

## Build

```powershell
dotnet restore .\DeskPilot.sln
dotnet build .\DeskPilot.sln --configuration Release --no-restore
dotnet test .\DeskPilot.sln --configuration Release --no-build --no-restore
```

## Runtime data

DeskPilot stores user-specific data under `%LOCALAPPDATA%\DeskPilot`. Runtime databases, logs, models, audio, and published binaries are not source-controlled.
