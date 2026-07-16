# DeskPilot

DeskPilot is a local Windows 10/11 voice assistant for fast desktop commands.

The current release establishes the solution architecture, local persistence, WPF shell, system tray, and extension contracts. Audio capture, speech recognition, device control, and application execution are intentionally deferred to later milestones.

## Requirements

- Windows 10 or Windows 11 x64
- .NET SDK 10.0.300 or newer compatible feature band

## Build

```powershell
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
```

## Runtime data

DeskPilot stores user-specific data under `%LOCALAPPDATA%\DeskPilot`. Runtime databases, logs, models, audio, and published binaries are not source-controlled.
