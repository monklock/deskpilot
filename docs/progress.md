# Project Progress

## Current Milestone

Milestone 3 — Voice-controlled audio MVP

## Overall Progress

- Progress: 50%
- Current task: Milestone 2 — Task 2.5 completion and feature pull request
- Last completed task: Milestone 2 — Task 2.5: complete offline wake-to-text contour and hybrid model delivery
- Next task: Merge the feature pull request into `develop`, then create a new `feature/*` branch from updated `develop` for Milestone 3
- Blockers: None

## Milestones

- [x] Milestone 0 — Project foundation
- [x] Milestone 1 — Manual audio control
- [x] Milestone 2 — Wake word and voice recognition
- [ ] Milestone 3 — Voice-controlled audio MVP
- [ ] Milestone 4 — Applications and command groups
- [ ] Milestone 5 — Safe shutdown
- [ ] Milestone 6 — External providers and API
- [ ] Milestone 7 — External module loading
- [ ] Milestone 8 — Packaging and release

## Current Task

### Goal

Complete the voice state machine, WPF surface, hybrid release assets, and final Milestone 2 gate. Preserve the one-session-at-a-time rule and stop at recognized-text publication without command dispatch.

### Task 2.5 Completion Gate

- [x] Implementation completed
- [x] Unit tests added
- [x] Build passed
- [x] Tests passed
- [x] Formatting passed
- [x] Documentation updated
- [x] Security checks passed
- [x] Public repository check passed
- [x] Live Bluetooth reconnect and recognized-text smoke confirmed

## Completed Tasks

### 2026-07-18 — Milestone 2, Task 2.5: Complete offline wake-to-text contour and hybrid model delivery

- Result: Added the single-owner voice state machine, observable safe state, WPF voice controls, exact microphone selection, model-management surface, startup seeding, activation gate, bounded shutdown, and recognized-text publication without intent resolution or command dispatch.
- Offline release: A release command downloads pinned Vosk small Russian and multilingual Whisper base assets with byte limits, verifies SHA-256/provider structures, includes pinned full license texts, signs the exact seed and optional-model catalog bytes with ECDSA P-256, and rejects unsigned or incomplete bundles.
- Model updates: The UI provides explicit check, download/update, cancellation, activation, and built-in restoration. Multilingual Whisper small remains optional and uses the signed `voice-models-v1` GitHub release catalog.
- Bluetooth behavior: An active wake or command capture disconnect enters exact-endpoint recovery. DeskPilot never falls back to another microphone and resumes only after Windows returns the same saved endpoint ID.
- Safety: One lifecycle owner prevents overlapping runs; wake capture is disposed before command capture, command capture before Whisper; audio remains in memory; exception paths and credentials are not logged; shutdown always attempts tray, host, and process resource cleanup within the shared timeout.
- Verification: Fresh restore, Release build with zero warnings/errors, formatting, signed release-bundle verification, public-repository/security scans, and 170 automated tests passed. The live WPF smoke confirmed Bluetooth microphone selection/reconnect, `альфа`, and Russian recognized-text display.
- Review: Independent read-only review and re-review found no remaining Critical or Important issues.

### 2026-07-18 — Milestone 2, Task 2.4: In-memory VAD, Whisper transcription, and local signals

- Result: Added bounded in-memory command capture, local Russian Whisper.cpp transcription, typed recognition failures, model-path-aware DI, and fixed Windows feedback tones.
- VAD boundary: Arbitrary normalized capture buffers are reframed into 20 ms PCM16 windows. Leading silence is discarded, sensitivity `0.65..0.90` maps inversely to the RMS threshold, 250 ms speech is required, 900 ms trailing silence completes capture, and a ten-second total limit handles both long commands and no-speech timeout.
- Whisper boundary: Whisper.net 1.9.1 CPU processes only mono 16 kHz PCM16 from memory, always uses language `ru`, joins final segments, averages valid confidence values, and rejects missing models, invalid formats, blank text, low confidence, and provider failures through typed codes.
- Ownership and privacy: Pooled VAD buffers are cleared before return, native processors and factories are disposed deterministically, command audio is not persisted or logged, and smoke tests read only explicitly configured local files.
- Signals: `Ready`, `Success`, and `Failure` map to fixed local Windows system tones and run outside the WPF dispatcher without user-supplied audio files.
- Smoke boundary: Real local Whisper validation is opt-in through `DESKPILOT_WHISPER_SMOKE_MODEL` and `DESKPILOT_WHISPER_SMOKE_AUDIO`; absent variables cause no model, audio, repository, or network access.
- Tests: Added 27 focused VAD, Whisper, DI, smoke, cancellation, disposal, and local-signal cases. The complete solution now runs 143 tests.

### 2026-07-18 — Milestone 2, Task 2.3: Limited-grammar offline Vosk wake provider

- Result: Added the offline native Vosk boundary and a model-path-aware wake provider for the exact configured phrase `альфа` over the current normalized capture session.
- Recognition boundary: Each wake session uses JSON grammar containing only the configured phrase, accepts final text at a configured `0.65..0.90` threshold, and derives phrase confidence from the minimum `result[].conf` value.
- Safety: Partial results, ambient text, incomplete or out-of-range confidence payloads never activate the pipeline. Rejected text is not logged, and normal finite streams are flushed through `FinalResult()` before reporting end-of-stream.
- Ownership: The provider disposes the recognizer and native model on success, cancellation, capture failure, provider failure, and normal stream completion without disposing the coordinator-owned audio session.
- Smoke boundary: Real local validation is opt-in through `DESKPILOT_VOSK_SMOKE_MODEL` and `DESKPILOT_VOSK_SMOKE_AUDIO`; absent variables cause no model, audio, repository, or network access.
- Tests: Added 27 focused Vosk tests for grammar, confidence, partial and ambient rejection, JSON parsing, native completion, cancellation, capture failures, disposal, DI, and the opt-in smoke boundary. The complete solution now runs 116 tests.

### 2026-07-18 — Milestone 2, Task 2.2: Recoverable Windows microphone capture

- Result: Added Windows capture-endpoint enumeration, exact microphone selection, WASAPI session ownership, typed failures, and dependency-injection registration through NAudio 2.2.1.
- Bluetooth behavior: An explicitly selected headset microphone never falls back to another endpoint. Device notifications are broadcast without a reconnect gap, and the same Windows endpoint ID becomes resolvable again after reconnect.
- Audio boundary: Native callback bytes are copied into a bounded in-memory channel; mono PCM16 at 16 kHz is produced on a background worker with continuous WDL resampler state. Audio is not logged, persisted, or transmitted.
- Recovery: Disconnect, unsupported-format, startup, normalization, and hot-unplug paths stop and dispose native/COM ownership without leaking the selected device.
- Tests: Added 20 focused tests for selection, reconnect races, multi-subscriber notifications, callback isolation, frame ownership, streaming conversion, cleanup, factory mapping, and DI. The complete solution now runs 89 tests.

### 2026-07-18 — Milestone 2, Task 2.1: Secure voice model management

- Result: Added voice settings, signed catalog verification, user-initiated model download/update commands, immutable installation, activation, last-known-good rollback state, and built-in restoration.
- Security: Every HTTPS redirect origin is allow-listed, request timeouts cover response bodies, payloads are streamed through SHA-256, ZIP traversal/links/limits are rejected, and real Whisper/Vosk structures are validated before registration.
- Transactions: Existing version directories are reused only after a full content comparison; failed replacement keeps the recovery backup, and UI progress callbacks cannot invalidate a committed database record.
- Release boundary: No model binary is tracked. Real seed assets, generated seed manifest, Application activation gate, startup seeding, and the WPF Model Manager surface remain in Task 2.5 as planned.
- Tests: Added focused coverage for settings, migrations, DI, signature/hash failures, redirects, timeouts, cancellation, archive safety, provider formats, collision recovery, rollback state, and explicit UI commands.

### 2026-07-18 — Milestone 2 planning: voice pipeline and hybrid model delivery

- Result: Approved the complete local voice-pipeline design and split implementation into five reviewable TDD tasks.
- Models: The publish output contains a Vosk small Russian wake model and multilingual Whisper base; multilingual Whisper small remains an optional explicit download.
- Safety: Signed catalog, SHA-256 payload verification, safe extraction, atomic activation, rollback, restoration, and public-repository exclusions are mandatory gates.
- Bluetooth behavior: An explicitly selected input endpoint never falls back silently and resumes only when the same Windows endpoint ID returns.
- Milestone boundary: Milestone 2 displays recognized Russian text but does not resolve or dispatch commands.

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

Merge `feature/wake-word-voice-recognition` into `develop` through its reviewed pull request. After `develop` is updated, create a new `feature/*` branch from it for Milestone 3.
