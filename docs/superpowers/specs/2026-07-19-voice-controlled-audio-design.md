# Milestone 3 Voice-Controlled Audio Design

## Goal

Deliver the first useful end-to-end DeskPilot voice workflow:

```text
Wake phrase
-> local command recognition
-> deterministic intent resolution
-> registered command dispatch
-> AudioControlModule
-> local success or failure feedback
```

Milestone 3 covers only reversible Windows audio operations. It preserves the
offline, single-session, in-memory, and exact-device guarantees established in
Milestones 1 and 2.

## Scope

The milestone supports these voice operations:

- switch to the saved headphones endpoint;
- switch to the saved speakers endpoint;
- switch between the two saved endpoints;
- increase volume by 10 percentage points;
- decrease volume by 10 percentage points;
- set volume to an explicit percentage from 0 through 100;
- mute audio;
- unmute audio;
- toggle mute.

The default change for short `louder` and `quieter` phrases is exactly 10
percentage points. The existing audio service remains responsible for clamping
the resulting volume to the inclusive `0..100` range.

## Non-Goals

Milestone 3 does not add:

- application launching or command groups;
- shutdown or other destructive commands;
- voice confirmation dialogs;
- LLM, network, API, or external-plugin intent providers;
- user-defined phrases;
- automatic Bluetooth pairing or endpoint fallback;
- persistence, transmission, or logging of recognized text or command audio.

## Architecture

### Core contracts

`DeskPilot.Core` continues to own provider-neutral command and intent contracts.
The existing `AvailableCommand` contract is extended to describe deterministic
phrase patterns and their static arguments. A phrase pattern may contain only
an approved numeric placeholder used for volume percentages.

`IntentResolutionResult` is extended to carry a complete `CommandRequest` only
when resolution succeeds. Callers never reconstruct arguments from an
untrusted resolver result.

The command result contract preserves a stable safe error code in addition to
its execution status. Presentation code must not parse human-readable messages
to determine behavior.

### Module command catalog

`DeskPilot.Modules.Abstractions` exposes a command-description provider
contract. Each module publishes descriptions only for commands it owns.
`AudioControlModule` publishes the Russian phrase patterns, static arguments,
and display names for its supported voice commands.

The application command catalog joins module descriptions with the statically
registered `ICommandHandler` set. Startup validation rejects duplicate command
IDs, duplicate normalized patterns, invalid placeholders, or descriptions for
commands without registered handlers. Only catalog entries backed by a
registered handler are visible to an intent resolver.

The resolver and voice pipeline never reference `AudioControlModule` directly.

### Application services

`DeskPilot.Application` contains these focused components:

- `CommandTextNormalizer` normalizes Russian command text;
- `RussianVolumeNumberParser` parses one safe percentage value;
- `ExactPhraseIntentResolver` resolves exact normalized patterns first;
- `FuzzyPhraseIntentResolver` performs bounded fallback matching;
- `CompositeIntentResolver` enforces exact-before-fuzzy ordering;
- `VoiceCommandExecutionService` resolves recognized text against the command
  catalog and invokes `ICommandDispatcher` only for a resolved request.

These services are independent of WPF, NAudio, Vosk, Whisper, SQLite, and
Windows COM.

`VoiceCommandExecutionService.ExecuteAsync` accepts the recognized text and a
synchronous pre-dispatch progress callback. After successful resolution, the
service invokes that callback exactly once with only the command ID, intent
status, and resolver confidence, then immediately dispatches the internally
held request. Arguments never leave the service through the callback. The
returned immutable result contains the final resolution and optional execution
outcome. This gives the coordinator a precise `ExecutingCommand` transition
without duplicating resolution or exposing trusted arguments to presentation.

### Audio module additions

Device switching from speech uses logical slots rather than endpoint IDs. The
module adds registered handlers for:

- `audio.switch-preferred-device` with `slot=Headphones|Speakers`;
- `audio.toggle-preferred-device` with no arguments.

The handlers resolve saved endpoints through `IAudioPreferredDeviceService`
at execution time and verify availability through `IAudioOutputDeviceService`.
They never select another endpoint when the requested saved endpoint is missing
or disconnected.

Existing handlers remain responsible for:

- `audio.change-volume` with `delta=10|-10`;
- `audio.set-volume` with `percentage=0..100`;
- `audio.set-mute` with `muted=true|false`;
- `audio.toggle-mute`.

## Phrase Model

The initial catalog contains short, explicit Russian variants. Representative
canonical patterns are:

| Intent | Patterns | Command arguments |
| --- | --- | --- |
| Headphones | `переключи на наушники`, `включи наушники`, `звук на наушники` | `audio.switch-preferred-device`, `slot=Headphones` |
| Speakers | `переключи на колонки`, `включи колонки`, `звук на колонки` | `audio.switch-preferred-device`, `slot=Speakers` |
| Toggle endpoint | `переключи устройство`, `переключи звук между устройствами` | `audio.toggle-preferred-device` |
| Louder | `сделай громче`, `громче`, `увеличь громкость` | `audio.change-volume`, `delta=10` |
| Quieter | `сделай тише`, `тише`, `уменьши громкость` | `audio.change-volume`, `delta=-10` |
| Set volume | `громкость {percentage}`, `установи громкость {percentage}`, `сделай громкость {percentage} процентов` | `audio.set-volume`, parsed `percentage` |
| Mute | `выключи звук`, `убери звук`, `без звука` | `audio.set-mute`, `muted=true` |
| Unmute | `включи звук`, `верни звук` | `audio.set-mute`, `muted=false` |
| Toggle mute | `переключи мьют`, `переключи беззвучный режим` | `audio.toggle-mute` |

The catalog is intentionally finite. Adding a phrase requires a source change
and test; recognized free-form text cannot name a command ID or inject command
arguments.

## Normalization and Number Parsing

Normalization performs these ordered transformations:

1. Unicode normalization;
2. invariant lower-casing;
3. replacement of Russian `ё` with `е`;
4. removal of punctuation and symbols;
5. collapse and trim of whitespace;
6. canonicalization of an optional trailing `процент`, `процента`, or
   `процентов` token for percentage patterns.

The number parser accepts:

- decimal digits from `0` through `100`;
- Russian cardinal words from zero through one hundred, including compound
  forms such as `сорок пять`.

It rejects negative values, decimals, ranges, multiple numbers, numbers above
100, and text that leaves unmatched tokens inside the numeric placeholder.

## Resolution Rules

Resolution follows this fixed order:

1. Normalize the recognized input once.
2. Match all exact literal and percentage patterns.
3. Return `Ambiguous` without a request if more than one exact command matches.
4. If no exact match exists, run bounded fuzzy matching.
5. Execute only when the best fuzzy similarity is at least `0.86` and exceeds
   the second-best command by at least `0.08`.

Fuzzy similarity uses normalized Damerau-Levenshtein distance:

```text
similarity = 1 - distance / max(inputLength, patternLength)
```

For a percentage pattern, the numeric span must parse exactly before fuzzy
matching. Fuzzy comparison applies only to the remaining literal pattern text;
it cannot repair or guess a number.

Candidates belonging to the same command and producing the same arguments are
collapsed before ambiguity evaluation. A fuzzy result below the threshold is
`NotFound`; competing command candidates inside the `0.08` margin are
`Ambiguous`.

Whisper provider confidence is validated at the speech-recognition boundary.
Resolver confidence represents phrase similarity and is not multiplied by the
provider confidence.

## Voice Pipeline Flow

After successful local transcription, `VoicePipelineCoordinator` performs:

1. publish recognized text and recognition confidence;
2. transition to `ResolvingCommand`;
3. call `VoiceCommandExecutionService`;
4. on a resolved request, transition to `ExecutingCommand` immediately before
   dispatch;
5. publish the resolved command ID, intent status, execution status, and safe
   error code;
6. play success only for `CommandExecutionStatus.Succeeded`;
7. play failure for not found, ambiguous, rejected, failed, or unavailable
   endpoint outcomes;
8. enter the existing cooldown and return to `WaitingForWakeWord`.

The coordinator remains the single lifecycle owner. Wake capture, command
capture, recognition, resolution, and execution never overlap. Cancellation
during resolution or dispatch propagates through the existing pipeline token.

## Observable State and UI

`VoicePipelineSnapshot` adds safe display fields for:

- last resolved command ID;
- last intent status and resolver confidence;
- last command execution status;
- stable error code and safe Russian message.

The WPF voice panel displays the latest recognized phrase and command outcome.
It does not display endpoint IDs, raw arguments, exception messages, or captured
audio. Manual audio controls continue using the same dispatcher and handlers.

## Error Handling and Safety

- `NotFound`, `Ambiguous`, and low-confidence fuzzy results never dispatch.
- Missing or unavailable saved endpoints fail without choosing another device.
- Unsupported Windows endpoint switching remains a typed command failure; the
  voice path does not open settings or another process automatically.
- Invalid numeric input never reaches an audio handler.
- Handler rejection and platform failure preserve stable safe error codes.
- Unexpected exceptions are converted to a generic safe failure at the voice
  boundary and do not terminate the application.
- Logs may contain command ID, resolution status, execution status, and stable
  error code. They must not contain recognized text, endpoint IDs, arguments,
  model paths, credentials, or audio data.
- Audio commands are reversible, so Milestone 3 does not use the existing
  `WaitingForConfirmation` state.

## Testing Strategy

Implementation follows focused TDD. Required coverage includes:

- normalization of case, whitespace, punctuation, and `ё`/`е`;
- digit and Russian cardinal parsing for every boundary and representative
  compound values;
- exact resolution for every catalog phrase;
- parameter extraction and rejection for percentage patterns;
- fuzzy acceptance, threshold rejection, and ambiguity margin behavior;
- duplicate or unregistered catalog entry rejection;
- construction of only approved command IDs and arguments;
- preferred endpoint success, missing preference, disconnected endpoint, and
  toggle behavior;
- volume delta, explicit percentage, mute, unmute, and toggle dispatch;
- coordinator transitions through resolution and execution;
- success/failure signal selection and cooldown recovery;
- cancellation and unexpected resolver/dispatcher failures;
- WPF projection of safe command outcome fields;
- regression coverage for the existing manual controls and Milestone 2
  wake-to-text contour.

The final automated gate is:

```powershell
dotnet restore .\DeskPilot.sln
dotnet build .\DeskPilot.sln --configuration Release --no-restore
dotnet test .\DeskPilot.sln --configuration Release --no-restore
dotnet format .\DeskPilot.sln --verify-no-changes --no-restore
```

Repository safety and release-asset verification from Milestone 2 remain
mandatory. The live WPF smoke verifies wake phrase activation followed by
headphones, speakers, volume `+10`, volume `-10`, explicit percentage, mute,
and unmute commands against real Windows audio state.

## Completion Criteria

Milestone 3 is complete when:

- every supported audio phrase resolves locally and deterministically;
- only a registered command with validated arguments can be dispatched;
- all audio command outcomes produce the correct local signal and safe UI state;
- unavailable preferred devices never cause endpoint fallback;
- recognized text and command audio remain private;
- restore, Release build, tests, formatting, repository safety checks, release
  asset checks, and the live Windows smoke pass;
- requirements, architecture, voice-pipeline, roadmap, and progress documents
  plus README and changelog describe Milestone 3 accurately.
