# ADR-0004: Windows Core Audio adapter

## Decision

Use `NAudio.Wasapi` behind platform-neutral AudioControl contracts for Windows endpoint discovery, default-device state, master volume, mute, and notifications. Isolate default-endpoint switching in `PolicyConfigAudioEndpointSwitcher` with runtime capability checks and a system-settings fallback.

## Consequences

The AudioControl module remains independent of COM and NAudio. Windows-specific resources have explicit disposal ownership in Infrastructure. Endpoint-switch failures are recoverable operation results rather than application failures. Preferred devices are persisted by Windows endpoint ID, not by mutable friendly name.
