# Milestone 2 Voice Pipeline Design

## Status

Approved design for Milestone 2. Implementation must remain on `feature/wake-word-voice-recognition` and must not begin Milestone 3 command execution.

## Goal

Deliver a local Windows voice pipeline that is usable offline immediately after installation, detects the Russian wake phrase `альфа`, records one command, detects the end of speech, recognizes Russian text with whisper.cpp, and displays the result without dispatching a command.

## Scope

Milestone 2 includes:

- Windows capture-device enumeration and explicit microphone selection;
- recovery when the selected USB or Bluetooth microphone disconnects and reconnects;
- Vosk wake-word recognition with the limited grammar `["альфа"]`;
- configurable wake confidence and sensitivity;
- command capture and voice activity detection;
- local Russian speech recognition through whisper.cpp;
- state signals, cooldown, cancellation, and resource cleanup;
- a hybrid model distribution strategy with offline seed models and user-initiated updates;
- WPF controls for voice mode, microphone selection, model management, progress, and diagnostics;
- release assets that contain a working base model set without tracking models in Git.

Milestone 2 does not include:

- resolving recognized text to a registered command;
- dispatching or executing audio commands;
- automatic model downloads without an explicit user action;
- cloud recognition, TTS, LLM, mobile API, installer authoring, or GPU runtime selection;
- recording or retaining microphone audio after recognition completes.

Command resolution and execution begin in Milestone 3.

## Selected Technology

- .NET 10 and WPF on Windows 10/11 x64.
- NAudio.Wasapi 2.2.1 for Windows capture-device access.
- Vosk 0.3.38 with the Vosk small Russian model 0.22 for wake-word recognition.
- Whisper.net 1.9.1 and Whisper.net.Runtime 1.9.1 as the managed binding and CPU whisper.cpp runtime.
- Multilingual `ggml-base.bin` as the offline seed command model.
- Multilingual `ggml-small.bin` as the optional higher-accuracy model.
- SQLite for voice settings and installed-model state.
- `HttpClient` for manifest and model downloads.
- ECDSA P-256 with SHA-256 for remote manifest signatures and SHA-256 for every downloadable archive.

The provider runtime is updated only with an application release. The Model Manager updates compatible model data, not native DLLs.

## Project Boundaries

### `DeskPilot.Voice.Abstractions`

Owns platform-neutral contracts and immutable models for:

- microphone descriptors and selections;
- capture sessions and PCM format;
- wake-word detection;
- voice activity detection and captured command audio;
- speech recognition;
- voice model catalog, installation, activation, and progress;
- pipeline state snapshots and state signals.

It must not reference WPF, NAudio, Vosk, Whisper.net, EF Core, HTTP, or Windows APIs.

### `DeskPilot.Voice.AudioCapture`

Owns the Windows capture adapter. It enumerates active capture endpoints, opens one selected endpoint, converts native input to 16 kHz mono PCM16, and exposes a cancellable stream of frames. It retains no `MMDevice` or capture COM object outside the adapter boundary.

The project also owns the energy-based VAD implementation because it operates directly on normalized PCM frames. VAD keeps at most one ten-second command in memory and never writes command audio to disk.

### `DeskPilot.Voice.Vosk`

Owns `VoskWakeWordProvider` and a testable Vosk native-client boundary. It loads the active local Vosk model, creates a recognizer for 16 kHz mono PCM16, enables word confidence, and restricts recognition to `["альфа"]`.

Vosk is never used to transcribe the command or unrestricted ambient speech.

### `DeskPilot.Voice.WhisperCpp`

Owns `WhisperCppSpeechToTextProvider` and a testable Whisper client boundary. The implementation uses Whisper.net with the CPU runtime, loads the active local multilingual ggml model, forces language `ru`, processes in-memory command samples asynchronously, concatenates finalized segments, and disposes the processor and factory deterministically.

### `DeskPilot.Application`

Owns `VoicePipelineCoordinator`. The coordinator is the single state-machine authority and depends only on voice abstractions, `TimeProvider`, logging, and cancellation. It does not know about WPF, native model APIs, Windows endpoints, file paths, or EF Core.

### `DeskPilot.Infrastructure.ModelManagement`

This is a namespace inside the existing `DeskPilot.Infrastructure` project, not a new project. It owns:

- embedded seed-manifest access;
- signed remote-manifest retrieval and verification;
- bounded downloads and progress;
- archive validation and safe extraction;
- installed-version persistence;
- activation, rollback, cleanup, and seed restoration;
- release-model seeding into local application data.

### `DeskPilot.Desktop`

Owns `VoiceControlViewModel` and WPF presentation. It exposes no provider-specific types and contains no direct model, audio, HTTP, or EF Core operations.

## Voice Model Distribution

### Seed models

The publish output contains a read-only seed bundle:

```text
assets\voice-models\seed-manifest.json
assets\voice-models\wake-ru-0.22.zip
assets\voice-models\ggml-base.bin
```

These large assets are never committed to Git. A release build downloads exact pinned upstream artifacts, verifies expected hashes and licenses, and injects them into the publish output. A future installer wraps this publish output without changing the runtime layout.

At first start, seeding copies or extracts the bundle into writable, versioned directories:

```text
%LOCALAPPDATA%\DeskPilot\models\vosk\wake-ru\0.22\
%LOCALAPPDATA%\DeskPilot\models\whisper\base\openai-base\ggml-base.bin
```

The application can always restore these seed versions from its read-only publish assets.

### Optional model

The Model Manager offers multilingual `ggml-small.bin` as an optional higher-accuracy command model. Downloading it is never required for first use.

### Remote manifest

DeskPilot publishes `models.manifest.json` and `models.manifest.sig` as release assets. The UTF-8 manifest bytes are signed with ECDSA P-256/SHA-256. The private key exists only in the release environment; the application contains only the public key.

Every catalog item contains:

- stable model ID and provider ID;
- display name, quality tier, language, and model version;
- compressed and extracted size limits;
- HTTPS release-asset URI;
- SHA-256 archive hash;
- minimum and maximum compatible provider versions;
- archive format and expected entry point;
- license identifier and notice URI.

The update client accepts only HTTPS release assets from the configured DeskPilot GitHub repository and its GitHub release-asset redirect host.

## Model Installation Transaction

An update follows this sequence:

1. Download and verify the signed catalog manifest.
2. Reject incompatible provider/runtime ranges before downloading a model.
3. Verify available disk space against both download and extracted-size limits.
4. Download to a unique file under `%LOCALAPPDATA%\DeskPilot\tmp\model-downloads` with progress and cancellation.
5. Verify content length and SHA-256 before opening the archive.
6. Reject absolute paths, parent traversal, symbolic links, excessive entry count, and expanded-size overflow.
7. Extract to a unique staging directory on the same volume as the final model directory.
8. Validate the required Vosk directory structure or load-test the Whisper ggml header through the provider boundary.
9. Rename staging to the immutable version directory.
10. Activate the new version only while the voice pipeline is disabled or idle.
11. Retain the previously active version as the rollback target.
12. Remove temporary files in success, cancellation, and failure paths.

An active version is never overwritten. A failed update leaves the active model and provider unchanged. The UI provides `Check for updates`, `Download/Update`, `Cancel`, `Use`, and `Restore built-in model` actions.

Automatic checking may run when the Voice page opens, but download and activation require explicit user actions.

## Persistence

SQLite stores:

- voice mode enabled state;
- selected microphone endpoint ID and friendly name;
- wake phrase and confidence threshold;
- sensitivity value and cooldown duration;
- selected Vosk and Whisper model IDs and versions;
- installed model versions, relative paths, hashes, source, status, and installation time;
- last known good model version for each provider.

No absolute installation path is persisted. Paths are resolved relative to the current DeskPilot application-data root.

The initial defaults are:

- wake phrase: `альфа`;
- Vosk grammar: `["альфа"]`;
- wake confidence threshold: `0.80`;
- sensitivity range: `0.65..0.90` mapped inversely to the threshold;
- minimum speech duration: 250 ms;
- silence timeout: 900 ms;
- maximum command duration: 10 seconds;
- cooldown: 2 seconds;
- speech-recognition language: `ru`;
- seed Whisper model: multilingual `base`;
- optional Whisper model: multilingual `small`.

## Microphone Selection and Recovery

The UI lists active Windows capture endpoints and identifies the system default endpoint. An explicitly selected microphone is persisted by stable Windows endpoint ID.

- If no microphone was explicitly selected, DeskPilot follows the current Windows default capture endpoint.
- If an explicitly selected USB or Bluetooth microphone disappears, DeskPilot stops and disposes the active capture session, enters `Error`, and reports that the saved microphone is unavailable.
- DeskPilot does not silently fall back to another microphone after an explicit selection.
- Device refresh waits for the same endpoint ID. When it becomes active again, the coordinator returns to `WaitingForWakeWord` automatically if voice mode is enabled and required models remain healthy.
- Changing the selected microphone cancels and rebuilds the current wake session.

## Pipeline State Machine

Milestone 2 uses these existing `VoiceAssistantState` values:

```text
Disabled
→ WaitingForWakeWord
→ WakeWordDetected
→ ListeningForCommand
→ DetectingSpeechEnd
→ RecognizingCommand
→ Cooldown
→ WaitingForWakeWord
```

Failure from any active state transitions to `Error`. A recoverable model or microphone condition is retried only after the required resource becomes available. Disabling voice mode from any state cancels the current operation and transitions to `Disabled`.

One iteration is:

1. Validate active Vosk and Whisper models and resolve the microphone.
2. Open one capture session and start the limited-grammar Vosk provider.
3. Ignore detections below the configured threshold and log only metadata.
4. On `альфа`, dispose the wake session and transition to `WakeWordDetected`.
5. Play the ready signal, open a fresh command capture session, and transition to `ListeningForCommand`.
6. VAD reads normalized frames, discards leading silence, and captures until 900 ms of trailing silence or the ten-second limit.
7. Reject input shorter than 250 ms as no speech and return to cooldown.
8. Pass the captured in-memory PCM to Whisper, force `ru`, and collect final segments.
9. Publish recognized text and confidence to the desktop state without resolving or dispatching it.
10. Clear the command buffer, play success or failure feedback, wait two seconds through `TimeProvider`, and resume wake listening.

The wake session and command session are never active at the same time.

## State Signals

`IVoiceSignalService` produces short local tones for:

- wake word detected and ready for command;
- recognition completed;
- recognition failed.

Signals are generated or stored as assembly resources so no user audio file is required. Command accepted/completed and confirmation tones remain for later milestones.

## Error Handling

Expected failures use typed error codes and safe Russian UI messages:

- microphone unavailable or disconnected;
- capture format or initialization failure;
- missing, corrupt, or incompatible model;
- manifest download, signature, or hash failure;
- insufficient disk space;
- unsafe archive content;
- Vosk initialization or recognition failure;
- VAD timeout or no speech;
- Whisper initialization, cancellation, or recognition failure.

Provider and infrastructure layers log structured technical details without audio, recognized ambient text from rejected wake attempts, secrets, or personal absolute paths. The desktop displays a safe summary and a relevant action such as reconnecting the microphone, restoring the seed model, retrying the update, or disabling voice mode.

## Concurrency and Lifetime

- `VoicePipelineCoordinator` has one owned run task and one cancellation source.
- Enable and disable operations are idempotent.
- Model activation and microphone changes serialize through an asynchronous gate.
- Model updates can download while voice mode is active, but validation and activation wait for an idle or disabled pipeline.
- Native providers are scoped to one loaded model/session and are disposed deterministically.
- WPF commands never perform capture, model I/O, hashing, extraction, or inference on the UI thread.
- Shutdown cancels the pipeline, awaits its task with the existing host timeout, and then disposes capture and native resources.

## Desktop Experience

The Voice section displays:

- voice mode enabled state;
- current pipeline state;
- selected and default microphone;
- microphone availability;
- last detected wake phrase and confidence;
- last recognized command text and confidence;
- active Vosk and Whisper models;
- installed and available model versions;
- download progress, size, status, and cancellation;
- safe error and recovery guidance.

Changing models or microphones uses explicit commands. The UI does not start a model download implicitly.

## Testing Strategy

### Unit tests

- State-machine tests use fake providers and `TimeProvider` for every transition, cancellation, cooldown, retry, enable, and disable path.
- Model Manager tests use fake HTTP and temporary directories for valid updates, cancellation, invalid signature, invalid hash, incompatible runtime, insufficient space, unsafe archive entries, expanded-size limits, activation, rollback, cleanup, and seed restoration.
- VAD tests use synthetic PCM for leading silence, short noise, valid speech, trailing silence, no speech, and the ten-second maximum.
- Vosk and Whisper providers are tested through native-client boundaries without loading a real model in CI.
- Desktop tests use substitutes for microphone hot-plug, Bluetooth recovery, model progress, activation, rollback, and errors.

### Integration and smoke tests

- Windows capture integration enumerates endpoints without changing system defaults or recording persistent audio.
- An opt-in local Vosk smoke test loads the installed model and detects `альфа` from a local test fixture outside Git.
- An opt-in local Whisper smoke test loads the installed model and recognizes Russian speech from a local fixture outside Git.
- A pipeline smoke test uses synthetic/fake providers to run wake detection through recognized-text publication.
- A publish verification checks seed-manifest signatures, asset SHA-256, archive structure, licenses, expected sizes, and first-run seeding.

No test changes the default microphone, sends audio over a network, or requires models to be committed to the repository.

## Delivery Phases

Milestone 2 is implemented as five reviewable tasks, all on the same feature branch:

1. Voice settings, model contracts, signed catalog, seeding, download, activation, rollback, and Model Manager UI.
2. Microphone enumeration, selection persistence, capture sessions, hot-plug recovery, and capture UI.
3. Vosk limited-grammar wake provider, threshold, sensitivity, cooldown contracts, and local smoke path.
4. VAD, Whisper.net provider, Russian transcription, signals, and local smoke path.
5. Application state machine, WPF integration, release asset verification, documentation, and the complete milestone gate.

Each task follows TDD, ends with a focused commit, and stops at a review checkpoint before the next task.

## Definition of Done

Milestone 2 is complete only when:

- the published application can seed working Vosk small RU and Whisper base models without network access;
- the user can explicitly check for, download, verify, activate, cancel, roll back, and restore models;
- the user can optionally install and use multilingual Whisper small;
- active capture devices are listed and an explicit microphone choice persists by endpoint ID;
- a disconnected selected Bluetooth or USB microphone is reported safely and recovers by the same endpoint ID;
- `альфа` activates a command capture session through limited-grammar Vosk;
- VAD enforces the 250 ms, 900 ms, and 10 second limits;
- whisper.cpp recognizes a Russian command locally and the WPF UI displays the result;
- no recognized text is resolved or dispatched as a command;
- command audio is not persisted or transmitted;
- restore, Release build, tests, formatting, WPF startup, model-asset verification, security checks, and public-repository checks pass;
- `docs/progress.md`, requirements, architecture, roadmap, and voice-pipeline documentation accurately describe the completed state.

## References

- Vosk model catalog: <https://alphacephei.com/vosk/models>
- Vosk API: <https://github.com/alphacep/vosk-api>
- whisper.cpp model format and sizes: <https://github.com/ggml-org/whisper.cpp/blob/master/models/README.md>
- whisper.cpp runtime: <https://github.com/ggml-org/whisper.cpp>
- Whisper.net: <https://github.com/sandrohanea/whisper.net>
