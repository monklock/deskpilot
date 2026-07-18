# Manual Audio Control

## Goal

Milestone 1 provides a manual desktop control surface for Windows output audio: device discovery, master volume, mute, and switching between the user's preferred speakers and headphones.

## Scope

- Enumerate active Windows render endpoints and show their current availability.
- Read, set, and change the default multimedia endpoint's master volume in the inclusive `0..100` range.
- Set and toggle mute for the default multimedia endpoint.
- Persist two optional preferred endpoints: `Speakers` and `Headphones`.
- Switch the default multimedia output endpoint to either preferred device.
- Reflect external volume, mute, default-endpoint, and device-availability changes in the desktop UI.
- Handle an unavailable preferred Bluetooth device without terminating the application.

Voice recognition, spoken commands, audio capture, application launching, and automatic Bluetooth reconnection are outside this milestone.

## Architecture

`DeskPilot.Modules.AudioControl` owns platform-neutral audio contracts, command handlers, state models, and the module registration boundary. The module never exposes COM objects or NAudio types.

`DeskPilot.Infrastructure` implements those contracts for Windows. `NAudio.Wasapi` provides render-endpoint enumeration, default-endpoint resolution, master volume, and mute. A separate `PolicyConfigAudioEndpointSwitcher` owns the native endpoint-switching interop. The switcher is capability-checked at runtime and returns a failure result instead of propagating a COM exception to the UI.

The desktop ViewModel uses the module contracts only. It loads the audio state, exposes explicit commands, and refreshes state on the WPF dispatcher every two seconds. This polling reflects external volume, mute, default-endpoint, and Bluetooth availability changes without leaking native types into the UI. The ViewModel neither starts PowerShell nor uses COM directly.

## Persistent Preferences

The local SQLite database stores each preferred endpoint by its stable Windows endpoint ID, friendly name, and logical slot (`Speakers` or `Headphones`). Friendly names are display data only; switching always uses the endpoint ID.

If a saved device is missing or inactive, the UI keeps the preference visible as unavailable, disables switching to it, and offers device refresh. No automatic fallback changes the user's default output device.

## Operation Results and Errors

All user-triggered operations return immutable result models with a success flag, a machine-readable status, and a user-safe message. Expected conditions such as an unavailable Bluetooth device, an unsupported endpoint switcher, or a transient COM failure are logged and surfaced in the UI without crashing the host process.

When the endpoint switcher is unavailable, the UI provides the system sound-settings fallback. It must not invoke PowerShell, CMD, or an external helper executable.

## Testing

Unit tests cover volume clamping, mute transitions, unavailable preferred devices, endpoint-ID persistence, and command-to-service routing. Infrastructure tests use fakes around the Windows adapter boundary; they do not require changing the developer's real default audio device. WPF ViewModel tests verify state and error presentation without Windows Core Audio.

## Acceptance Criteria

- The desktop UI lists active output devices and shows the active default endpoint.
- Volume can be set, increased, decreased, muted, and unmuted.
- The user can save and select speakers and headphones by endpoint ID.
- An unavailable Bluetooth headset is reported safely and remains selectable again after it becomes active.
- External endpoint and volume changes are reflected in the UI.
- Build, tests, formatting, security checks, and public-repository checks pass.
