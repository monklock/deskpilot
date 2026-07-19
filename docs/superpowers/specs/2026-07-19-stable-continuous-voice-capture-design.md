# Stable Continuous Voice Capture Design

## Status

Approved architecture for the Milestone 3 stabilization slice. This design
replaces the wake-to-command capture gap with one continuous microphone stream,
a bounded ring buffer, and adaptive voice activity detection.

## Goal

Make both supported interaction styles reliable:

```text
альфа -> pause -> сделай тише
альфа сделай тише
```

The command onset must not be lost while DeskPilot changes state after the wake
phrase. Ambient noise, the ready signal, and short non-speech sounds must not
prematurely finish command capture.

## Current Failure

The current coordinator closes the wake capture after Vosk detects `альфа`,
plays the ready signal, and then opens a fresh command capture. Audio spoken
during that gap is unavailable to VAD and Whisper.

The current energy detector also uses one fixed RMS threshold. At high
sensitivity, an application signal or ambient noise can start command capture;
900 milliseconds of subsequent silence can then finish it before the user
speaks.

Increasing only the trailing-silence timeout does not correct either failure.

## Scope

This stabilization slice includes:

- one continuously owned capture session while voice mode is enabled;
- a bounded sequence-indexed PCM16 ring buffer;
- a timed handoff from Vosk to command VAD without reopening the microphone;
- support for paused and one-shot wake-to-command speech;
- adaptive noise-floor estimation and hysteresis-based VAD;
- separate initial-silence, end-silence, and maximum-command limits;
- in-memory UI diagnostics for the current cycle;
- deterministic automated audio tests and an explicit live acceptance matrix;
- preservation of exact-endpoint Bluetooth recovery and local-only processing.

## Non-Goals

This slice does not add:

- a network speech provider, LLM, or cloud endpointing;
- a new native WebRTC, Silero, RNNoise, or AEC dependency;
- storage or logging of microphone audio or recognized command text;
- automatic Bluetooth pairing, reconnection, or microphone fallback;
- changes to the approved command catalog, resolver thresholds, or audio
  handlers, except where tests must prove that rejected audio cannot dispatch;
- completion or publication of Milestone 3 before the live acceptance gate.

The VAD remains behind `IVoiceActivityDetector` so a later measured comparison
can replace the adaptive implementation without changing the coordinator.

## Architecture

```text
Windows endpoint
  -> NAudioCaptureSession
  -> BufferedVoiceCaptureSession (single pump, two-second ring buffer)
       -> AmbientNoiseEstimator (rolling level statistics only)
       -> Vosk cursor
       -> wake start/end/detection sample offsets
       -> command cursor opened at wake end
       -> AdaptiveVoiceActivityDetector
       -> CapturedCommandAudio
  -> Whisper
  -> trusted resolver
  -> dispatcher
```

The buffered session is the only reader of `IAudioCaptureSession`. It copies
normalized mono 16 kHz PCM16 frames into a fixed-capacity ring and assigns
monotonically increasing sample offsets. Vosk and VAD read through independent
cursors; neither owns or disposes the underlying microphone session.

The same pump sends per-frame RMS values, never PCM copies, to a focused ambient
noise estimator. The estimator retains a rolling three-second window of level
statistics and exposes a robust lower-percentile noise-floor snapshot. Speech,
the wake phrase, and isolated sound spikes therefore cannot dominate the
baseline merely by being the latest frames.

The coordinator owns one buffered session from `WaitingForWakeWord` through
wake detection, command capture, recognition, execution, cooldown, and the next
wake cycle. It disposes and recreates that session only when voice mode is
disabled, the selected endpoint changes or disconnects, model activation takes
the pipeline lease, or an unrecoverable capture failure occurs.

## Components and Contracts

### Buffered capture session

`DeskPilot.Voice.AudioCapture` adds a buffered session around the existing
normalized capture contract.

The component:

- owns one background pump over `IAudioCaptureSession.ReadFramesAsync`;
- stores exactly two seconds of PCM16, or 64,000 bytes at 16 kHz mono;
- assigns each frame an inclusive start and exclusive end sample offset;
- lets a consumer open a cursor at an available sample offset;
- lets a wake consumer follow the live edge from the current offset;
- clears overwritten bytes and clears the complete rented buffer on disposal;
- never blocks the native NAudio callback on Vosk, VAD, UI, or Whisper work;
- reports a typed overrun if a cursor requests samples already overwritten;
- publishes the same terminal capture failure to every active cursor.

Only the background pump reads the hardware session. Cursor disposal never
stops the microphone.

### Wake detection marker

`WakeWordDetectionResult` is extended with absolute sample offsets for:

- wake phrase start;
- wake phrase end;
- the latest sample consumed when detection was published.

Vosk word timing is mapped from the recognizer-relative timeline to the
buffered session timeline. The offsets must satisfy:

```text
wake start <= wake end <= detection position
```

If a provider returns invalid timing, the wake result is rejected as a typed
provider failure. If the buffer has already overwritten the wake-end position,
the cycle fails safely without invoking Whisper or dispatching a command.
Word timing is explicitly enabled on each limited-grammar Vosk recognizer; an
accepted wake result without a timed `альфа` token is invalid.

### Command cursor handoff

After wake detection, the coordinator disposes only the Vosk cursor. It keeps
the buffered capture session open and creates the command cursor at the wake
end offset. Audio captured between wake completion and Vosk publication is
therefore preserved.

The command detector keeps a 300-millisecond rolling pre-speech window starting
no earlier than the wake-end offset. This preserves the first command phoneme
without sending the wake phrase to Whisper.

### Adaptive voice activity detector

The replacement detector analyses 20-millisecond PCM16 frames. It receives the
ambient estimator snapshot captured at wake detection and continues adapting
the baseline only while command speech remains unconfirmed. Frames classified
as possible or confirmed speech are excluded from further baseline updates.

Speech start and speech continuation use different thresholds. The start
threshold is higher; the continuation threshold is lower. This hysteresis
prevents a quiet syllable from ending speech immediately after a louder one.
The existing sensitivity value remains bounded to `0.65..0.90` and changes the
threshold multipliers, not a fixed absolute RMS cutoff.

The initial runtime values are:

| Parameter | Value |
| --- | ---: |
| Analysis frame | 20 ms |
| Command pre-roll | 300 ms |
| Confirmed speech duration | 150 ms |
| Initial silence timeout | 4 s |
| End silence timeout | 1.2 s |
| Maximum command duration | 10 s |
| Ambient noise history | 3 s of RMS statistics |

Speech is considered started only after the configured confirmed-speech
duration is accumulated. The pre-roll is then prepended to the captured
command. End silence is measured only after confirmed speech. Reaching initial
silence without confirmed speech returns `SpeechDetected=false`; reaching the
maximum duration returns the bounded audio only when confirmed speech exists.

The detector exposes safe diagnostic measurements for the current cycle:
observed duration, captured duration, speech start offset, speech end offset,
noise-floor RMS, and peak RMS. It never exposes or logs PCM data.

`IVoiceActivityDetector.CaptureAsync` receives a synchronous progress callback.
The detector invokes it exactly once when speech becomes confirmed and before
waiting for end silence. The coordinator maps this callback to
`DetectingSpeechEnd`; absence of the callback means speech never started.

## Ready Signal Policy

The ready sound is not played between wake detection and command capture. The
UI state `ListeningForCommand` is the default ready indication. Success and
failure sounds remain after recognition or execution has finished.

DeskPilot does not introduce a microphone suppression window after wake
detection because such a window would clip one-shot commands. Audible ready
feedback can be reconsidered only with measured echo cancellation or a design
that proves it cannot enter command audio.

## State Flow

The corrected state sequence is:

```text
WaitingForWakeWord
-> WakeWordDetected
-> ListeningForCommand
-> DetectingSpeechEnd       only after confirmed speech start
-> RecognizingCommand
-> ResolvingCommand
-> ExecutingCommand
-> Cooldown
-> WaitingForWakeWord       using the same capture session
```

`ListeningForCommand` begins immediately after the wake result is accepted.
There is no capture-open operation in that transition.

If no speech is confirmed within four seconds, the state store publishes
`speech-not-detected` and `Команда не распознана`, plays the failure signal,
enters cooldown, and returns to wake listening on the same healthy session.

## Failure and Recovery Rules

- Microphone disconnect terminates the buffer pump and every cursor with the
  existing typed `Disconnected` failure.
- The coordinator disposes the failed session and waits for the exact saved
  endpoint ID. It never falls back to the Windows default microphone.
- Buffer overrun, invalid Vosk timing, and malformed PCM are typed safe
  failures. They cannot reach Whisper, resolver, or dispatcher.
- Disable, shutdown, and model activation cancel the pump, await cursor exit,
  clear in-memory buffers, and dispose native ownership once.
- Whisper, resolver, and handler failures preserve their existing safe outcome
  mapping and cooldown behavior.
- A new wake cycle clears previous cycle diagnostics before accepting speech.

## Privacy and Safety

- The ring contains at most two seconds of normalized PCM and is cleared on
  overwrite and disposal.
- Captured command audio remains bounded to ten seconds and in memory.
- Audio, recognized text, sample offsets, RMS values, endpoint IDs, model
  paths, and handler arguments are excluded from logs.
- The WPF diagnostics contain only the current in-memory cycle and are cleared
  on the next wake or when voice mode is disabled.
- Raw recognized text is never converted directly into a `CommandRequest`;
  the trusted resolver remains the only dispatch path.

## UI Diagnostics

The existing voice surface displays:

- microphone capture status;
- wake detection status;
- `Слушаю команду` before confirmed speech;
- `Определяю окончание речи` only after confirmed speech;
- captured command duration;
- recognized text and Whisper confidence;
- the existing safe resolver and execution outcome.

No waveform, raw PCM, endpoint ID, or persistent history is added.

## Test Strategy

### Deterministic unit tests

The buffered-session tests cover:

- one hardware reader with multiple sequential cursors;
- exact frame/sample offset mapping;
- wraparound at the two-second boundary;
- bytes cleared on overwrite and disposal;
- cursor handoff preserving frames produced after wake end;
- typed cursor overrun;
- cancellation, disconnect, and single disposal.

The adaptive-VAD tests use deterministic PCM16 frames and cover:

- immediate command onset at the wake boundary;
- 300, 700, and 1,500 millisecond pauses;
- quiet speech after louder speech;
- stable ambient noise and a short noise spike;
- four-second no-speech timeout;
- 1.2-second end silence;
- ten-second maximum duration;
- 300-millisecond pre-roll without wake-word leakage;
- arbitrary source frame sizes reframed into 20-millisecond analysis frames.

Coordinator tests prove:

- wake and command use the same buffered session;
- the command cursor starts at the wake-end offset;
- no ready signal is played before command speech;
- `DetectingSpeechEnd` is published only after confirmed speech;
- no-speech, overrun, invalid timing, and disconnect cannot dispatch;
- cooldown returns to wake listening without reopening a healthy microphone;
- reconnect resumes only the saved endpoint.

### Real-provider smoke tests

Opt-in local smoke assets cover:

- `альфа сделай тише` without a pause;
- `альфа`, then pauses of 300, 700, and 1,500 milliseconds;
- wake-only input;
- a command with quiet first and final syllables.

The smoke harness reports timings and recognized text to the invoking test
process only. Assets, audio, and output remain ignored and uncommitted.

### Live acceptance gate

Run at least 20 attempts for every command-timing scenario on the primary
microphone and repeat the critical no-pause and 700-millisecond scenarios on a
Bluetooth microphone.

Milestone 3 cannot be marked complete unless:

- all 20 clean-room attempts avoid premature speech end;
- at least 19 of 20 clean-room commands resolve and execute correctly;
- wake-only input executes zero commands;
- ambient and system sounds execute zero commands during a 30-minute soak;
- Bluetooth disconnect and reconnect resumes only the same saved endpoint;
- the UI exposes enough state to identify wake, speech start, speech end,
  recognition, resolution, and execution;
- Release build, full tests, format verification, signed bundle verification,
  runtime-data scan, credential scan, and live WPF smoke all pass.

## Implementation Sequence

1. Add deterministic failing audio-handoff and endpointing tests.
2. Add sequence-indexed buffering around the existing capture session.
3. Extend Vosk wake results with validated absolute timing.
4. Keep one buffered capture session across wake and command states.
5. Replace fixed energy VAD with the adaptive hysteresis detector.
6. Correct state transitions, ready-signal behavior, and UI diagnostics.
7. Run automated, real-provider, Bluetooth, and soak verification.
8. Update public milestone documentation only after the acceptance gate passes.

Each numbered item is a review checkpoint. Implementation stops after a failed
gate instead of continuing into later stages.

## Expected File Map

Likely modified files:

- `src/DeskPilot.Voice.Abstractions/Audio/VoiceAudioContracts.cs`;
- `src/DeskPilot.Voice.Abstractions/VoiceContracts.cs`;
- `src/DeskPilot.Voice.AudioCapture/AudioCaptureServiceCollectionExtensions.cs`;
- `src/DeskPilot.Voice.AudioCapture/EnergyVoiceActivityDetector.cs`;
- `src/DeskPilot.Voice.Vosk/VoskRecognitionJsonParser.cs`;
- `src/DeskPilot.Voice.Vosk/VoskWakeWordProvider.cs`;
- `src/DeskPilot.Application/Voice/VoicePipelineCoordinator.cs`;
- `src/DeskPilot.Application/Voice/VoicePipelineStateStore.cs`;
- `src/DeskPilot.Desktop/ViewModels/VoiceControlViewModel.cs`;
- `src/DeskPilot.Desktop/MainWindow.xaml`;
- focused Application, Voice, and Desktop tests;
- `docs/voice-pipeline.md`, `docs/progress.md`, and `docs/roadmap.md` only at
  the appropriate verification checkpoint.

Likely new files:

- `src/DeskPilot.Voice.AudioCapture/BufferedVoiceCaptureSession.cs`;
- `src/DeskPilot.Voice.AudioCapture/VoiceAudioRingBuffer.cs`;
- `src/DeskPilot.Voice.AudioCapture/AmbientNoiseEstimator.cs`;
- `src/DeskPilot.Voice.AudioCapture/AdaptiveVoiceActivityDetector.cs`;
- focused tests for each new component.

The implementation plan must confirm the exact file list against the branch
before any source edit and must not include unrelated Milestone 3 refactoring.
