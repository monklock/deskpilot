# Requirements

DeskPilot targets Windows 10/11 x64 and uses C# with .NET 10, WPF, MVVM, SQLite, Entity Framework Core, Generic Host, dependency injection, and structured logging.

The wake phrase is `альфа`. A future wake-word provider listens only for this limited grammar. A future speech-to-text provider processes a command only after activation and voice activity detection.

Task 0.1 excludes audio capture, speech recognition implementations, Windows audio control, application launching, command groups, shutdown, network APIs, and installers.

## Milestone 1 — Manual Audio Control

- List active Windows render endpoints and display the default multimedia endpoint.
- Read and change master volume in the inclusive `0..100` range and toggle mute.
- Persist optional speakers and headphones preferences by stable endpoint ID.
- Switch to an available preferred endpoint through registered commands.
- Keep a disconnected Bluetooth headset preference visible but disabled, then restore availability after Windows reconnects the same endpoint.
- Refresh external audio state changes without blocking the WPF UI.
- Fall back to Windows sound settings when automatic endpoint switching is unavailable.
- Do not initiate Bluetooth pairing or reconnection and do not start voice recognition in this milestone.
