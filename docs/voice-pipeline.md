# Voice Pipeline

Milestone 2 implements this local, offline contour:

```text
Selected microphone → limited-grammar Vosk (`альфа`) → fresh command capture
→ voice activity detection → local Russian Whisper → WPF recognized text
```

Intent resolution, command dispatch, and module execution begin only in Milestone 3.

## Runtime states

The Application coordinator is the only state-machine owner:

```text
Disabled → WaitingForWakeWord → WakeWordDetected → ListeningForCommand
→ DetectingSpeechEnd → RecognizingCommand → Cooldown → WaitingForWakeWord
```

Any recoverable microphone or model failure enters `Error` with a typed, user-safe message. Enable and disable are idempotent, cancellation awaits the active run, and only one voice session can exist. Wake capture is disposed before command capture starts; command capture is disposed before Whisper runs.

## Audio and recognition

The Vosk provider accepts only the fixed phrase `альфа` and never publishes rejected ambient text. Command audio is normalized to mono PCM16 at 16 kHz and remains in bounded memory. Voice activity detection requires 250 ms of speech, completes after 900 ms of trailing silence, and limits total capture to 10 seconds. Sensitivity is stored independently in the inclusive `0.65..0.90` range.

Whisper runs locally with Russian language selection. Successful text and confidence are published to the state store and WPF only; no `ICommandDispatcher` dependency exists in the coordinator.

## Bluetooth and endpoint recovery

The selected capture endpoint is persisted by stable Windows endpoint ID. If it disappears, DeskPilot reports it as unavailable, disposes capture, and waits. It never substitutes the default microphone. After Windows reconnects the same Bluetooth or USB endpoint, voice mode resumes automatically when the active models remain healthy. DeskPilot does not pair or reconnect Bluetooth devices itself.

## Hybrid model delivery

Release output contains verified seed assets for Vosk small Russian and multilingual Whisper base so a new installation works offline. Startup verifies the signed seed manifest, hashes, and provider structure before installing immutable local versions. The Model Manager lets the user explicitly check, download/update, cancel, activate, and restore built-in models. A larger multilingual Whisper small model remains optional.

Remote catalogs are ECDSA P-256/SHA-256 signed, downloads are HTTPS-only with allow-listed redirect origins, and every payload is size-, hash-, and format-verified before activation. Model activation waits for an idle pipeline and preserves last-known-good rollback state.

Real-provider smoke tests are opt-in. Vosk uses `DESKPILOT_VOSK_SMOKE_MODEL` and `DESKPILOT_VOSK_SMOKE_AUDIO`; Whisper uses `DESKPILOT_WHISPER_SMOKE_MODEL` and `DESKPILOT_WHISPER_SMOKE_AUDIO`. Missing variables perform no filesystem or network access.
