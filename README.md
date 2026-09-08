# DeskPilot

DeskPilot is a local Windows 10/11 voice assistant for deterministic desktop commands.

The current development version implements manual and voice-controlled Windows audio: offline wake word and Russian speech recognition, saved speakers/headphones switching, volume and mute commands, safe WPF feedback, local persistence, model management, and a modular command-dispatch boundary. Application launching and command groups remain planned for Milestone 4.

Public project milestones are listed in [docs/roadmap.md](docs/roadmap.md).

## Requirements

- Windows 10 or Windows 11 x64
- .NET SDK 10.0.300 or newer compatible feature band

## Build

```powershell
dotnet restore .\DeskPilot.sln
dotnet build .\DeskPilot.sln --configuration Release --no-restore
dotnet test .\DeskPilot.sln --configuration Release --no-build --no-restore
```

## GigaSTT voice engine

The desktop voice pipeline uses one local GigaSTT process and one shared Russian
GigaAM RNNT INT8 model for wake detection and command transcription. Say `альфа`,
wait for the ready signal, then speak a command. The existing continuous microphone
buffer, endpoint recovery, and trusted audio-command dispatcher are preserved.

GigaSTT runs offline on loopback, starts with voice mode, and stops when its capture
session ends. Release preparation supplies its Windows executable, model and VAD
assets through the signed model installer; installing the .NET application alone
does not download models. The model manager exposes the shared GigaSTT bundle.
Older Vosk/Whisper database records and files remain readable and are not deleted.

Legacy provider projects remain available for comparison until real microphone
recordings establish acceptable wake accuracy, false activation rate, command
accuracy, latency, and resource use. Passing unit tests alone is not that acceptance.

## Runtime data

DeskPilot stores user-specific data under `%LOCALAPPDATA%\DeskPilot`. Runtime databases, logs, models, audio, and published binaries are not source-controlled.
