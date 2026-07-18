# Requirements

DeskPilot targets Windows 10/11 x64 and uses C# with .NET 10, WPF, MVVM, SQLite, Entity Framework Core, Generic Host, dependency injection, and structured logging.

The wake phrase is `альфа`. The local Vosk provider listens only for this limited grammar. After activation, DeskPilot captures one bounded command, detects the end of speech, and recognizes Russian text locally with Whisper. Milestone 2 publishes that text to the desktop UI but never resolves or dispatches it as a command.

Task 0.1 excludes audio capture, speech recognition implementations, Windows audio control, application launching, command groups, shutdown, network APIs, and installers.

## Milestone 1 — Manual Audio Control

- List active Windows render endpoints and display the default multimedia endpoint.
- Read and change master volume in the inclusive `0..100` range and toggle mute.
- Persist optional speakers and headphones preferences by stable endpoint ID.
- Switch to an available preferred endpoint through registered commands.
- Keep a disconnected Bluetooth headset preference visible but disabled, then restore availability after Windows reconnects the same endpoint.
- Refresh external audio state changes without blocking the WPF UI.
- Fall back to Windows sound settings when automatic endpoint switching is unavailable.
- Do not initiate Bluetooth pairing or reconnection in this milestone.

## Milestone 2 — Wake Word and Voice Recognition

- Work offline immediately after a release installation by seeding a small Russian Vosk wake model and multilingual Whisper base model from verified publish assets.
- Let the user check for, download, cancel, activate, and restore voice models through explicit UI actions. Optional larger models are never downloaded automatically.
- Persist one explicitly selected capture endpoint and never fall back silently when that USB or Bluetooth microphone is unavailable.
- Resume voice mode only when the same Windows endpoint ID becomes active again after Windows reconnects the device.
- Keep exactly one cancellable voice session active and dispose wake capture before command capture and command capture before recognition.
- Apply configurable wake and voice-activity sensitivity in the inclusive `0.65..0.90` range.
- Keep command audio in memory only. Do not persist, transmit, or log audio or rejected ambient speech.
- Expose safe state, model, download, and recovery diagnostics without credentials or personal absolute paths.
- Do not resolve intents or dispatch recognized text until Milestone 3.
