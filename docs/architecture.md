# Architecture

DeskPilot is an extensible modular monolith. `DeskPilot.Core` contains domain contracts and has no dependency on WPF, SQLite, Windows APIs, or speech providers. `DeskPilot.Application` dispatches only registered commands. `DeskPilot.Infrastructure` implements local paths and SQLite persistence. `DeskPilot.Desktop` is the WPF composition root.

Modules register statically through dependency injection and do not call each other directly. The desktop UI and future control surfaces use `ICommandDispatcher`. The Milestone 2 voice pipeline deliberately stops before that boundary and publishes recognized text only through its state store.

SQLite is stored at `%LOCALAPPDATA%\DeskPilot\data\deskpilot.db`. It contains application and voice settings plus the Milestone 1 preferred-audio-endpoint records.

## Manual Audio Control

`DeskPilot.Modules.AudioControl` owns platform-neutral audio contracts, immutable result models, command validation, and static command-handler registration. `DeskPilot.Infrastructure.WindowsAudio` is the only layer that references NAudio or the native Windows `PolicyConfig` COM interface. The desktop ViewModel queries those contracts and routes every mutation through `ICommandDispatcher`.

Preferred speakers and headphones are stored in SQLite by logical slot and stable Windows endpoint ID. A friendly name is display-only. Active render endpoints are refreshed every two seconds on the WPF dispatcher, which reflects external volume, mute, default-device, and Bluetooth availability changes without retaining COM objects in the UI.

If native endpoint switching is unsupported, the application returns a typed failure and opens `ms-settings:sound` as an explicit fallback. It does not invoke PowerShell, CMD, or helper executables.

## Offline Voice Pipeline

`DeskPilot.Voice.Abstractions` owns provider-neutral settings, capture, wake detection, voice activity, recognition, signal, and model-management contracts. Windows capture and native Vosk/Whisper adapters stay behind those contracts. `DeskPilot.Application` owns the single `VoicePipelineCoordinator` and observable `VoicePipelineStateStore`; neither references WPF, NAudio, Vosk, Whisper, native paths, or command dispatch.

The coordinator owns one run task and cancellation source. It resolves the persisted endpoint and healthy active models, opens separate wake and command capture sessions, disposes command capture before native recognition, and serializes microphone changes and model activation. An activation lease stops the pipeline while the model repository atomically switches versions, then resumes voice mode when safe.

The WPF `VoiceControlViewModel` exposes asynchronous, non-reentrant controls for voice mode, endpoint selection, sensitivity, recognized text, model updates, cancellation, activation, and built-in restoration. A disconnected saved Bluetooth microphone remains visible and no default-device fallback occurs. Windows performs pairing and reconnection; DeskPilot resumes only when the same stable endpoint ID returns.

Release preparation injects verified Vosk small Russian and multilingual Whisper base files into publish output. It signs both the seed manifest and remote optional-model catalog. Startup verifies the seed signature before parsing metadata, then installs immutable versions under `%LOCALAPPDATA%\DeskPilot\models`; optional larger models require the signed GitHub release catalog and an explicit user action. The ECDSA signing private key is external to the repository and release output.
