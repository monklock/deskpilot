# Architecture

DeskPilot is an extensible modular monolith. `DeskPilot.Core` contains domain contracts and has no dependency on WPF, SQLite, Windows APIs, or speech providers. `DeskPilot.Application` dispatches only registered commands. `DeskPilot.Infrastructure` implements local paths and SQLite persistence. `DeskPilot.Desktop` is the WPF composition root.

Modules register statically through dependency injection and do not call each other directly. The desktop UI and voice control surface use `ICommandDispatcher`. Voice input reaches that boundary only after a module-owned description is validated and a local resolver creates a trusted typed request.

SQLite is stored at `%LOCALAPPDATA%\DeskPilot\data\deskpilot.db`. It contains application and voice settings plus the Milestone 1 preferred-audio-endpoint records.

## Manual Audio Control

`DeskPilot.Modules.AudioControl` owns platform-neutral audio contracts, immutable result models, command validation, and static command-handler registration. `DeskPilot.Infrastructure.WindowsAudio` is the only layer that references NAudio or the native Windows `PolicyConfig` COM interface. The desktop ViewModel queries those contracts and routes every mutation through `ICommandDispatcher`.

Preferred speakers and headphones are stored in SQLite by logical slot and stable Windows endpoint ID. A friendly name is display-only. Active render endpoints are refreshed every two seconds on the WPF dispatcher, which reflects external volume, mute, default-device, and Bluetooth availability changes without retaining COM objects in the UI.

If native endpoint switching is unsupported, the application returns a typed failure and opens `ms-settings:sound` as an explicit fallback. It does not invoke PowerShell, CMD, or helper executables.

## Offline Voice Pipeline

`DeskPilot.Voice.Abstractions` owns provider-neutral settings, capture, wake detection, voice activity, recognition, signal, and model-management contracts. Windows capture and GigaSTT adapters stay behind those contracts. `DeskPilot.Application` owns the single `VoicePipelineCoordinator`, observable `VoicePipelineStateStore`, command catalog, intent resolvers, and resolve-to-dispatch service; it references abstractions rather than WPF, NAudio, the recognition process, or native paths.

The coordinator owns one run task and cancellation source. It resolves the persisted endpoint and shared GigaSTT model, keeps one buffered microphone session, and uses timed wake boundaries to retain subsequent command audio. Wake PCM is streamed over loopback WebSocket; bounded command audio is transcribed through local REST. Partial hypotheses never activate the assistant. An activation lease stops capture and the owned recognition process before the model repository atomically switches versions, then resumes voice mode when safe.

The WPF `VoiceControlViewModel` exposes asynchronous, non-reentrant controls for voice mode, endpoint selection, sensitivity, recognized text, model updates, cancellation, activation, and built-in restoration. A disconnected saved Bluetooth microphone remains visible and no default-device fallback occurs. Windows performs pairing and reconnection; DeskPilot resumes only when the same stable endpoint ID returns.

## Trusted Voice Commands

Each module owns an `ICommandDescriptionProvider`. At composition time, `CommandCatalog` rejects duplicate command IDs, duplicate normalized phrases, unsupported placeholders, and descriptions without a registered handler. Runtime flow is:

```text
module descriptions → CommandCatalog → exact resolver → bounded fuzzy resolver
→ VoiceCommandExecutionService → ICommandDispatcher → AudioControlModule
```

Exact matching precedes fuzzy matching. The fuzzy threshold is `0.86`, the ambiguity margin is `0.08`, and numeric percentages remain strict. Unknown or ambiguous input never reaches the dispatcher. Preferred-device handlers use only the saved endpoint requested by the trusted phrase and never fall back to another output.

The state store exposes display-safe command ID, intent status/confidence, execution status, stable error code, and fixed safe message. It never exposes handler details or command arguments. Structured logs omit recognized text, endpoint IDs, raw arguments, model paths, credentials, and audio.

Release preparation injects a verified GigaSTT Windows runtime, RNNT INT8 model, and Silero VAD into publish output. It signs both the seed manifest and remote model catalog. Startup verifies the seed signature before parsing metadata, then installs immutable versions under `%LOCALAPPDATA%\DeskPilot\models`; updates require a signed catalog and explicit user action. Legacy provider identifiers remain readable for existing databases, but the desktop selects only the shared GigaSTT bundle. The ECDSA signing material remains external to the repository and release output.
