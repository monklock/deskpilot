# Project Progress

## Current Milestone

Milestone 2 — Wake word and voice recognition

## Overall Progress

- Progress: 22%
- Current task: Milestone 2 — define and implement the wake-word and voice-recognition slice
- Last completed task: Milestone 1 — Task 1.1: Manual audio control vertical slice
- Next task: Prepare the Milestone 2 implementation plan
- Blockers: None

## Milestones

- [x] Milestone 0 — Project foundation
- [x] Milestone 1 — Manual audio control
- [ ] Milestone 2 — Wake word and voice recognition
- [ ] Milestone 3 — Voice-controlled audio MVP
- [ ] Milestone 4 — Applications and command groups
- [ ] Milestone 5 — Safe shutdown
- [ ] Milestone 6 — External providers and API
- [ ] Milestone 7 — External module loading
- [ ] Milestone 8 — Packaging and release

## Current Task

### Goal

Prepare the Milestone 2 wake-word and voice-recognition implementation plan without changing the completed manual audio contour.

### Milestone 1 Completion Gate

- [x] Implementation completed
- [x] Unit tests added
- [x] Build passed
- [x] Tests passed
- [x] Formatting passed
- [x] Documentation updated
- [x] Security checks passed
- [x] Public repository check passed

## Completed Tasks

### 2026-07-18 — Milestone 1, Task 1.1: Manual audio control vertical slice

- Result: Added the complete WPF manual audio contour for active render-device discovery, default-device display and switching, master volume, mute, and persistent speakers/headphones preferences.
- Bluetooth behavior: A saved headset remains visible but disabled while disconnected; the two-second state refresh recognizes the same endpoint ID after Windows reconnects it and enables manual switching again.
- Safety: Expected Core Audio and endpoint-switching failures are converted to safe results; unsupported automatic switching opens `ms-settings:sound` without PowerShell, CMD, or helper executables.
- Tests: Command validation and module registration, SQLite preference persistence, Windows adapter result mapping, external-state refresh, Bluetooth disconnect/reconnect, and settings fallback.
- Known limitation: DeskPilot does not initiate Bluetooth pairing or reconnection; Windows must connect the headset first.

### 2026-07-16 — Milestone 0, Task 0.1: Project foundation and architecture

- Result: Created the solution, modular contracts, local SQLite settings migration, Generic Host, minimal WPF shell, tray icon, documentation, and local repository safeguards.
- Changed files: Solution structure, source projects, tests, documentation, and public repository metadata.
- Tests: Command dispatcher, module initialization, voice defaults, local paths, and SQLite migration.
- Architectural decisions: Modular monolith, local SQLite storage, and WPF Generic Host lifecycle are recorded in ADRs.
- Known limitations: No audio, speech, command module behavior, API, or installer is implemented.

## Known Issues

- None

## Next Task

Milestone 2 — prepare the wake-word and voice-recognition implementation plan.
