# Architecture

DeskPilot is an extensible modular monolith. `DeskPilot.Core` contains domain contracts and has no dependency on WPF, SQLite, Windows APIs, or speech providers. `DeskPilot.Application` dispatches only registered commands. `DeskPilot.Infrastructure` implements local paths and SQLite persistence. `DeskPilot.Desktop` is the WPF composition root.

Modules register statically through dependency injection and do not call each other directly. Voice, desktop UI, and future control surfaces use `ICommandDispatcher`.

SQLite is stored at `%LOCALAPPDATA%\DeskPilot\data\deskpilot.db`. It contains application and voice settings plus the Milestone 1 preferred-audio-endpoint records.

## Manual Audio Control

`DeskPilot.Modules.AudioControl` owns platform-neutral audio contracts, immutable result models, command validation, and static command-handler registration. `DeskPilot.Infrastructure.WindowsAudio` is the only layer that references NAudio or the native Windows `PolicyConfig` COM interface. The desktop ViewModel queries those contracts and routes every mutation through `ICommandDispatcher`.

Preferred speakers and headphones are stored in SQLite by logical slot and stable Windows endpoint ID. A friendly name is display-only. Active render endpoints are refreshed every two seconds on the WPF dispatcher, which reflects external volume, mute, default-device, and Bluetooth availability changes without retaining COM objects in the UI.

If native endpoint switching is unsupported, the application returns a typed failure and opens `ms-settings:sound` as an explicit fallback. It does not invoke PowerShell, CMD, or helper executables.
