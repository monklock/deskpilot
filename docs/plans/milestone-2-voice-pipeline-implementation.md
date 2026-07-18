# Milestone 2 Voice Pipeline Implementation Plan

**Goal:** Deliver a complete local Windows voice contour that detects `альфа`, captures one spoken command, recognizes Russian text offline, displays it in WPF, and safely manages bundled and downloadable models without dispatching commands.

**Architecture:** Keep audio, model, wake-word, VAD, and speech contracts in `DeskPilot.Voice.Abstractions`. Windows capture and native providers stay behind adapters; `DeskPilot.Application` owns the state machine; `DeskPilot.Infrastructure` owns SQLite and transactional model management; WPF owns presentation only.

**Tech stack:** .NET 10, WPF, CommunityToolkit.Mvvm 8.4.0, EF Core SQLite 10.0.8, NAudio.Wasapi 2.2.1, Vosk 0.3.38, Whisper.net 1.9.1, Whisper.net.Runtime 1.9.1 CPU, xUnit, FluentAssertions, NSubstitute.

## Global Constraints

- Target Windows 10/11 x64 and preserve existing `net10.0` / `net10.0-windows` boundaries.
- Use Vosk only for the limited grammar `["альфа"]`; use multilingual Whisper for command transcription with language `ru`.
- Ship the Vosk small Russian 0.22 seed and multilingual Whisper base seed in publish output; offer multilingual Whisper small as an optional download.
- Keep models, native runtime data, captured audio, logs, credentials, private signing keys, and local smoke fixtures out of Git.
- Download only after explicit user action; automatic checks must not download or activate anything.
- Verify the remote manifest with ECDSA P-256/SHA-256 and every model payload with SHA-256 before installation.
- Install through a same-volume staging directory, immutable version directory, atomic activation, rollback, and built-in restoration.
- Persist an explicitly selected microphone by Windows endpoint ID. Never silently fall back if that endpoint disappears.
- Keep command audio only in memory for at most ten seconds and never log, persist, or transmit it.
- Stop at recognized-text publication. Do not invoke `IIntentResolver`, `ICommandDispatcher`, or a module from the voice pipeline in Milestone 2.
- Write a failing focused test before each production behavior and commit after every task gate.

---

## File Structure

| Path | Responsibility |
| --- | --- |
| `src/DeskPilot.Voice.Abstractions/Audio/VoiceAudioContracts.cs` | Microphone, normalized frame, capture-session, and VAD contracts. |
| `src/DeskPilot.Voice.Abstractions/Models/VoiceModelContracts.cs` | Model catalog, install, activation, and progress contracts. |
| `src/DeskPilot.Voice.Abstractions/Pipeline/VoicePipelineContracts.cs` | Wake, transcription, signals, settings, errors, and pipeline snapshots. |
| `src/DeskPilot.Infrastructure/Data/*` | Voice settings and installed-model persistence plus migration. |
| `src/DeskPilot.Infrastructure/ModelManagement/*` | Seed catalog, signed remote catalog, secure download, extraction, activation, rollback, and restoration. |
| `src/DeskPilot.Voice.AudioCapture/*` | NAudio input enumeration, endpoint monitoring, capture, normalization, and VAD. |
| `src/DeskPilot.Voice.Vosk/*` | Limited-grammar wake provider and native boundary. |
| `src/DeskPilot.Voice.WhisperCpp/*` | Whisper.net transcription provider and native boundary. |
| `src/DeskPilot.Application/Voice/*` | Single-owner state machine and recognized-text publication. |
| `src/DeskPilot.Desktop/ViewModels/VoiceControlViewModel.cs` | Voice, microphone, model, progress, and diagnostic presentation state. |
| `src/DeskPilot.Desktop/MainWindow.xaml` | Voice control and Model Manager WPF surface. |
| `scripts/voice-model-assets.ps1` | Release-only download, hash verification, license collection, and seed injection. |
| `assets/voice-models/seed-manifest.json` | Small tracked metadata file; contains no model binaries. |
| `tests/DeskPilot.Voice.Tests/*` | Contracts, capture, VAD, Vosk, Whisper, and coordinator tests. |
| `tests/DeskPilot.Infrastructure.Tests/VoiceModelManagementTests.cs` | Persistence and secure model transaction tests. |
| `tests/DeskPilot.Desktop.Tests/VoiceControlViewModelTests.cs` | WPF-independent UI state and command tests. |

## Task 1: Add voice settings and transactional Model Manager

**Files:**

- Modify: `Directory.Packages.props`
- Modify: `src/DeskPilot.Infrastructure/DeskPilot.Infrastructure.csproj`
- Modify: `src/DeskPilot.Infrastructure/Data/AppDataPaths.cs`
- Modify: `src/DeskPilot.Infrastructure/Data/DeskPilotDbContext.cs`
- Create: `src/DeskPilot.Infrastructure/Data/Migrations/202607180002_AddVoiceModels.cs`
- Create: `src/DeskPilot.Voice.Abstractions/Models/VoiceModelContracts.cs`
- Create: `src/DeskPilot.Voice.Abstractions/Pipeline/VoicePipelineContracts.cs`
- Create: `src/DeskPilot.Infrastructure/Preferences/SqliteVoiceSettingsRepository.cs`
- Create: `src/DeskPilot.Infrastructure/ModelManagement/VoiceModelManifest.cs`
- Create: `src/DeskPilot.Infrastructure/ModelManagement/VoiceModelManifestVerifier.cs`
- Create: `src/DeskPilot.Infrastructure/ModelManagement/VoiceModelCatalogClient.cs`
- Create: `src/DeskPilot.Infrastructure/ModelManagement/VoiceModelStore.cs`
- Create: `src/DeskPilot.Infrastructure/ModelManagement/VoiceModelInstaller.cs`
- Create: `src/DeskPilot.Infrastructure/ModelManagement/VoiceModelManager.cs`
- Create: `src/DeskPilot.Infrastructure/ModelManagement/SeedVoiceModelInitializer.cs`
- Create: `src/DeskPilot.Desktop/ViewModels/VoiceModelManagerViewModel.cs`
- Create: `assets/voice-models/seed-manifest.json`
- Create: `tests/DeskPilot.Infrastructure.Tests/VoiceModelManagementTests.cs`
- Create: `tests/DeskPilot.Desktop.Tests/VoiceModelManagerViewModelTests.cs`

**Interfaces:**

- Consumes: `IAppDataPaths`, `IDbContextFactory<DeskPilotDbContext>`, `HttpClient`, and `TimeProvider`.
- Produces: `IVoiceSettingsRepository`, `IVoiceModelManager`, `VoiceModelDescriptor`, `VoiceModelState`, `VoiceModelProgress`, and `VoiceModelManagerViewModel`.

- [ ] **Step 1: Write failing tests for defaults, signature rejection, hash rejection, rollback, restoration, cancellation, and explicit download.**

```csharp
[Fact]
public async Task InstallAsync_InvalidHash_LeavesActiveVersionUnchanged()
{
    await using var fixture = await ModelFixture.CreateAsync(activeVersion: "base-seed");
    fixture.Http.RespondWithBytes([1, 2, 3]);
    var item = fixture.Item with { Sha256 = new string('0', 64) };

    var result = await fixture.Installer.InstallAsync(item, progress: null, CancellationToken.None);

    result.Code.Should().Be(VoiceModelResultCode.HashMismatch);
    (await fixture.Store.GetActiveAsync(item.ProviderId, CancellationToken.None))!.Version.Should().Be("base-seed");
    fixture.AssertNoStagingDirectories();
}

[Fact]
public async Task DownloadCommand_DoesNotRunUntilUserInvokesIt()
{
    var manager = Substitute.For<IVoiceModelManager>();
    manager.GetStateAsync(Arg.Any<CancellationToken>()).Returns(VoiceModelState.Empty);
    var viewModel = new VoiceModelManagerViewModel(manager);

    await viewModel.InitializeAsync();

    await manager.DidNotReceive().InstallAsync(
        Arg.Any<VoiceModelDescriptor>(),
        Arg.Any<IProgress<VoiceModelProgress>?>(),
        Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Run the focused tests and confirm they fail because model contracts and services do not exist.**

Run:

```powershell
dotnet test .\tests\DeskPilot.Infrastructure.Tests\DeskPilot.Infrastructure.Tests.csproj --filter "FullyQualifiedName~VoiceModelManagementTests"
dotnet test .\tests\DeskPilot.Desktop.Tests\DeskPilot.Desktop.Tests.csproj --filter "FullyQualifiedName~VoiceModelManagerViewModelTests"
```

Expected: compilation failures naming `IVoiceModelManager`, `VoiceModelInstaller`, or `VoiceModelManagerViewModel`.

- [ ] **Step 3: Add package versions, references, paths, and the model/settings contracts.**

```xml
<PackageVersion Include="Microsoft.Extensions.Http" Version="10.0.8" />
<PackageVersion Include="Vosk" Version="0.3.38" />
<PackageVersion Include="Whisper.net" Version="1.9.1" />
<PackageVersion Include="Whisper.net.Runtime" Version="1.9.1" />
```

```csharp
public enum VoiceModelProvider { WakeVosk, CommandWhisper }
public enum VoiceModelSource { Seed, Download }
public enum VoiceModelResultCode { Success, Cancelled, InvalidManifest, InvalidSignature, HashMismatch, Incompatible, InsufficientSpace, UnsafeArchive, InvalidModel, IoFailure }

public sealed record VoiceModelDescriptor(
    string Id,
    VoiceModelProvider ProviderId,
    string DisplayName,
    string Version,
    string QualityTier,
    Uri DownloadUri,
    string Sha256,
    long DownloadBytes,
    long InstalledBytes,
    string EntryPoint,
    string LicenseId,
    bool IsBuiltIn);

public sealed record VoiceModelProgress(string ModelId, long BytesReceived, long TotalBytes, string Stage);
public sealed record VoiceModelOperationResult(VoiceModelResultCode Code, string? Message = null);
public sealed record VoiceModelState(IReadOnlyList<VoiceModelDescriptor> Catalog, IReadOnlyDictionary<VoiceModelProvider, string> ActiveVersions)
{
    public static VoiceModelState Empty { get; } = new([], new Dictionary<VoiceModelProvider, string>());
}

public interface IVoiceModelManager
{
    Task<VoiceModelState> GetStateAsync(CancellationToken cancellationToken);
    Task<VoiceModelState> CheckForUpdatesAsync(CancellationToken cancellationToken);
    Task<VoiceModelOperationResult> InstallAsync(VoiceModelDescriptor model, IProgress<VoiceModelProgress>? progress, CancellationToken cancellationToken);
    Task<VoiceModelOperationResult> ActivateAsync(VoiceModelProvider provider, string version, CancellationToken cancellationToken);
    Task<VoiceModelOperationResult> RestoreBuiltInAsync(VoiceModelProvider provider, CancellationToken cancellationToken);
}

public interface IVoiceModelActivationGate
{
    Task<IAsyncDisposable> EnterIdleAsync(CancellationToken cancellationToken);
}

public sealed record VoiceSettings(
    bool IsEnabled,
    string? MicrophoneEndpointId,
    string? MicrophoneFriendlyName,
    string WakePhrase,
    double WakeConfidence,
    TimeSpan Cooldown,
    string RecognitionLanguage)
{
    public static VoiceSettings Default { get; } = new(false, null, null, "альфа", 0.80, TimeSpan.FromSeconds(2), "ru");
}

public interface IVoiceSettingsRepository
{
    Task<VoiceSettings> GetAsync(CancellationToken cancellationToken);
    Task SaveAsync(VoiceSettings settings, CancellationToken cancellationToken);
}
```

Add `ModelsRootPath`, `ModelDownloadsPath`, and `SeedModelsPath` to `IAppDataPaths`; all persisted model paths remain relative to `ModelsRootPath`.

Add a project reference from `DeskPilot.Infrastructure` to `DeskPilot.Voice.Abstractions`, plus `Microsoft.Extensions.Http`. Register one named `HttpClient` with a finite request timeout, redirect validation, and no ambient credentials.

- [ ] **Step 4: Add SQLite entities, migration, and repositories with immutable version activation.**

```csharp
public sealed class InstalledVoiceModelEntity
{
    public required string ProviderId { get; set; }
    public required string Version { get; set; }
    public required string ModelId { get; set; }
    public required string RelativePath { get; set; }
    public required string Sha256 { get; set; }
    public required string Source { get; set; }
    public bool IsActive { get; set; }
    public bool IsLastKnownGood { get; set; }
    public DateTimeOffset InstalledAt { get; set; }
}
```

Configure the composite key `(ProviderId, Version)`, a unique filtered active index per provider, and one transaction that clears the old active row, marks it last-known-good, and activates the requested installed row. Store voice settings as explicit keys under `VoiceSettings`; do not serialize native provider objects or absolute paths.

- [ ] **Step 5: Implement exact-byte manifest verification and the safe installation transaction.**

```csharp
public bool Verify(ReadOnlySpan<byte> manifest, ReadOnlySpan<byte> signature)
{
    using var algorithm = ECDsa.Create();
    algorithm.ImportFromPem(_publicKeyPem);
    return algorithm.VerifyData(manifest, signature, HashAlgorithmName.SHA256);
}
```

`VoiceModelInstaller.InstallAsync` must execute this ordered transaction:

1. Validate provider compatibility, HTTPS origin allow-list, lengths, and free disk space.
2. Download to `%LOCALAPPDATA%\DeskPilot\tmp\model-downloads\<guid>.part` with cancellation and progress.
3. Compute SHA-256 while streaming and compare using ordinal case-insensitive hexadecimal equality.
4. For archives, reject rooted paths, `..`, links/reparse points, more than 10,000 entries, or expanded bytes above the manifest limit.
5. Extract or copy into `<ModelsRootPath>\staging\<guid>` on the final volume.
6. Validate the expected entry point and provider-specific model header/directory structure.
7. Rename staging to `<provider>\<model-id>\<version>` without overwriting an existing version.
8. Enter `IVoiceModelActivationGate`, persist installed state, then activate only through the repository transaction while capture/native provider sessions are stopped.
9. Delete `.part` and staging data in `finally`; never delete the currently active or last-known-good version.

- [ ] **Step 6: Add the tracked seed manifest and Model Manager ViewModel.**

```json
{
  "schemaVersion": 1,
  "models": [
    { "id": "wake-ru-small", "provider": "WakeVosk", "version": "0.22", "asset": "wake-ru-0.22.zip", "builtIn": true },
    { "id": "whisper-base-multi", "provider": "CommandWhisper", "version": "openai-base", "asset": "ggml-base.bin", "builtIn": true }
  ]
}
```

`VoiceModelManagerViewModel` exposes `CheckForUpdatesCommand`, `DownloadOrUpdateCommand`, `CancelDownloadCommand`, `UseModelCommand`, and `RestoreBuiltInCommand`. It creates a new cancellation source for each explicit download, reports percentage from `VoiceModelProgress`, disables conflicting actions while busy, and maps typed result codes to safe Russian messages.

`SeedVoiceModelInitializer` runs after database migration and before the coordinator. It reads the read-only publish seed manifest, verifies the bundled asset hashes, copies/extracts missing immutable seed versions into `ModelsRootPath`, registers them as built-in, and activates a seed only when that provider has no healthy active version. `RestoreBuiltInAsync` repeats the same verification and installation path instead of trusting a writable local copy.

- [ ] **Step 7: Run the Task 1 gate.**

Run:

```powershell
dotnet test .\tests\DeskPilot.Infrastructure.Tests\DeskPilot.Infrastructure.Tests.csproj --filter "FullyQualifiedName~VoiceModelManagementTests"
dotnet test .\tests\DeskPilot.Desktop.Tests\DeskPilot.Desktop.Tests.csproj --filter "FullyQualifiedName~VoiceModelManagerViewModelTests"
dotnet build .\DeskPilot.sln -c Release --no-restore
```

Expected: all focused tests pass and Release build exits with code `0` and no warnings.

- [ ] **Step 8: Commit Task 1.**

```powershell
git add Directory.Packages.props assets/voice-models src/DeskPilot.Voice.Abstractions src/DeskPilot.Infrastructure src/DeskPilot.Desktop/ViewModels/VoiceModelManagerViewModel.cs tests/DeskPilot.Infrastructure.Tests tests/DeskPilot.Desktop.Tests
git commit -m "feat: add secure voice model manager"
```

## Task 2: Add microphone selection, capture, normalization, and Bluetooth recovery

**Files:**

- Create: `src/DeskPilot.Voice.Abstractions/Audio/VoiceAudioContracts.cs`
- Modify: `src/DeskPilot.Voice.AudioCapture/DeskPilot.Voice.AudioCapture.csproj`
- Create: `src/DeskPilot.Voice.AudioCapture/NAudioInputDeviceService.cs`
- Create: `src/DeskPilot.Voice.AudioCapture/IWindowsCaptureEndpointSource.cs`
- Create: `src/DeskPilot.Voice.AudioCapture/NAudioCaptureFactory.cs`
- Create: `src/DeskPilot.Voice.AudioCapture/NAudioCaptureSession.cs`
- Create: `src/DeskPilot.Voice.AudioCapture/Pcm16Normalizer.cs`
- Create: `src/DeskPilot.Voice.AudioCapture/AudioCaptureServiceCollectionExtensions.cs`
- Modify: `tests/DeskPilot.Voice.Tests/DeskPilot.Voice.Tests.csproj`
- Create: `tests/DeskPilot.Voice.Tests/AudioInputDeviceServiceTests.cs`
- Create: `tests/DeskPilot.Voice.Tests/Pcm16NormalizerTests.cs`
- Create: `tests/DeskPilot.Voice.Tests/AudioCaptureSessionTests.cs`

**Interfaces:**

- Consumes: `IVoiceSettingsRepository`, `TimeProvider`, and NAudio 2.2.1 `MMDeviceEnumerator` / `WasapiCapture` behind internal boundaries.
- Produces: `IAudioInputDeviceService`, `IAudioCaptureSessionFactory`, `IAudioCaptureSession`, `AudioInputDevice`, and normalized `AudioFrame` values.

- [ ] **Step 1: Write failing tests for default selection, explicit selection, disconnect, same-ID reconnect, frame ownership, and PCM conversion.**

```csharp
[Fact]
public async Task ResolveAsync_ExplicitBluetoothEndpointMissing_DoesNotFallBackToDefault()
{
    var source = Substitute.For<IWindowsCaptureEndpointSource>();
    source.GetActiveAsync().Returns([new("laptop", "Laptop microphone", true)]);
    var service = new NAudioInputDeviceService(source);

    var result = await service.ResolveAsync("bluetooth-mic", CancellationToken.None);

    result.Code.Should().Be(AudioInputResultCode.SelectedDeviceUnavailable);
    result.Device.Should().BeNull();
}

[Fact]
public void Normalize_StereoFloat48k_ReturnsMonoPcm16At16k()
{
    var input = TestAudio.StereoFloatSine(sampleRate: 48_000, durationMs: 60);
    var result = Pcm16Normalizer.Normalize(input.Bytes, input.Format);

    result.Format.Should().Be(AudioFormat.Pcm16KhzMono);
    result.Pcm16.Length.Should().Be(1_920);
}
```

- [ ] **Step 2: Run the focused voice tests and confirm the new contracts are missing.**

Run:

```powershell
dotnet test .\tests\DeskPilot.Voice.Tests\DeskPilot.Voice.Tests.csproj --filter "FullyQualifiedName~AudioInputDeviceServiceTests|FullyQualifiedName~Pcm16NormalizerTests|FullyQualifiedName~AudioCaptureSessionTests"
```

Expected: compilation failures naming audio input and capture types.

- [ ] **Step 3: Add capture contracts with owned frame memory.**

```csharp
public sealed record AudioFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat)
{
    public static AudioFormat Pcm16KhzMono { get; } = new(16_000, 1, 16, false);
}

public sealed record AudioInputDevice(string EndpointId, string FriendlyName, bool IsDefault, bool IsAvailable);
public sealed record AudioFrame(ReadOnlyMemory<byte> Pcm16, TimeSpan Duration);
public enum AudioInputResultCode { Success, NoDevice, SelectedDeviceUnavailable, InitializationFailed, Disconnected, UnsupportedFormat }
public sealed record AudioInputResolution(AudioInputResultCode Code, AudioInputDevice? Device, string? Message = null);

public interface IAudioInputDeviceService
{
    Task<IReadOnlyList<AudioInputDevice>> GetActiveAsync(CancellationToken cancellationToken);
    Task<AudioInputResolution> ResolveAsync(string? explicitEndpointId, CancellationToken cancellationToken);
    IAsyncEnumerable<IReadOnlyList<AudioInputDevice>> WatchAsync(CancellationToken cancellationToken);
}

public interface IAudioCaptureSession : IAsyncDisposable
{
    string EndpointId { get; }
    AudioFormat Format { get; }
    IAsyncEnumerable<AudioFrame> ReadFramesAsync(CancellationToken cancellationToken);
}

public interface IAudioCaptureSessionFactory
{
    Task<IAudioCaptureSession> OpenAsync(string endpointId, CancellationToken cancellationToken);
}
```

Each `AudioFrame` must own a copied byte array; never expose the NAudio callback buffer after `DataAvailable` returns.

Change `DeskPilot.Voice.Tests` to `net10.0-windows`, add project references to `DeskPilot.Voice.AudioCapture`, `DeskPilot.Voice.Vosk`, and `DeskPilot.Voice.WhisperCpp`, and add `NSubstitute`. Add `NAudio.Wasapi` to `DeskPilot.Voice.AudioCapture`:

```xml
<PackageReference Include="NAudio.Wasapi" />
```

- [ ] **Step 4: Implement the NAudio 2.2.1 adapter and conversion boundary.**

Use `MMDeviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)`, `MMDevice.ID`, `FriendlyName`, and `GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)`. Open an explicit endpoint with `new WasapiCapture(device)`, subscribe to `DataAvailable` and `RecordingStopped`, and publish copied buffers into a bounded `Channel<byte[]>`.

`Pcm16Normalizer` converts the native format to mono floating-point samples, averages channels, resamples to 16,000 Hz through `WdlResamplingSampleProvider`, clamps to `[-1, 1]`, and encodes signed little-endian PCM16. Reject formats that NAudio cannot expose as PCM or IEEE float.

- [ ] **Step 5: Implement device change monitoring and recovery semantics.**

`WatchAsync` coalesces Core Audio notifications and re-enumerates active capture endpoints. If the persisted endpoint ID is absent, the consumer receives `SelectedDeviceUnavailable`; when the exact same ID reappears, it is resolvable again. If the stored endpoint is `null`, each new session resolves the current Windows default.

On `RecordingStopped` with an exception or device invalidation, complete the channel with a typed `AudioCaptureException(AudioInputResultCode.Disconnected, ...)`, unsubscribe events, stop capture once, and dispose NAudio/COM objects.

- [ ] **Step 6: Run the Task 2 gate.**

Run:

```powershell
dotnet test .\tests\DeskPilot.Voice.Tests\DeskPilot.Voice.Tests.csproj --filter "FullyQualifiedName~AudioInputDeviceServiceTests|FullyQualifiedName~Pcm16NormalizerTests|FullyQualifiedName~AudioCaptureSessionTests"
dotnet build .\src\DeskPilot.Voice.AudioCapture\DeskPilot.Voice.AudioCapture.csproj -c Release --no-restore
```

Expected: all capture tests pass; the Windows capture project builds without warnings.

- [ ] **Step 7: Commit Task 2.**

```powershell
git add src/DeskPilot.Voice.Abstractions src/DeskPilot.Voice.AudioCapture tests/DeskPilot.Voice.Tests
git commit -m "feat: add recoverable microphone capture"
```

## Task 3: Add the limited-grammar Vosk wake provider

**Files:**

- Modify: `src/DeskPilot.Voice.Vosk/DeskPilot.Voice.Vosk.csproj`
- Create: `src/DeskPilot.Voice.Vosk/IVoskRecognizerClient.cs`
- Create: `src/DeskPilot.Voice.Vosk/VoskRecognizerClient.cs`
- Create: `src/DeskPilot.Voice.Vosk/VoskWakeWordProvider.cs`
- Create: `src/DeskPilot.Voice.Vosk/VoskServiceCollectionExtensions.cs`
- Create: `tests/DeskPilot.Voice.Tests/VoskWakeWordProviderTests.cs`

**Interfaces:**

- Consumes: `IAudioCaptureSession`, active `WakeVosk` model path, phrase `альфа`, and configured confidence.
- Produces: `IWakeWordProvider.WaitForDetectionAsync(IAudioCaptureSession, WakeWordOptions, CancellationToken)` and `WakeWordDetectionResult`.

- [ ] **Step 1: Write failing tests for grammar, confidence threshold, rejected ambient text, cancellation, and disposal.**

```csharp
[Fact]
public async Task WaitForDetectionAsync_IgnoresPhraseBelowThreshold()
{
    var recognizer = Substitute.For<IVoskRecognizerClient>();
    recognizer.Accept(Arg.Any<ReadOnlyMemory<byte>>()).Returns(
        new VoskRecognition("альфа", 0.79, true),
        new VoskRecognition("альфа", 0.91, true));
    await using var audio = FakeCaptureSession.WithSilentFrames(2);
    var provider = new VoskWakeWordProvider(_ => recognizer);

    var result = await provider.WaitForDetectionAsync(audio, new("альфа", 0.80), CancellationToken.None);

    result.Confidence.Should().Be(0.91);
    recognizer.Received(1).ConfigureGrammar("[\"альфа\"]");
}
```

- [ ] **Step 2: Run the focused test and confirm the provider is missing.**

Run:

```powershell
dotnet test .\tests\DeskPilot.Voice.Tests\DeskPilot.Voice.Tests.csproj --filter "FullyQualifiedName~VoskWakeWordProviderTests"
```

Expected: compilation failure naming `IVoskRecognizerClient` or `VoskWakeWordProvider`.

- [ ] **Step 3: Adjust the wake contract to consume the current capture session.**

```csharp
public sealed record WakeWordOptions(string Phrase, double MinimumConfidence);

public interface IWakeWordProvider
{
    string ProviderId { get; }
    Task<WakeWordDetectionResult> WaitForDetectionAsync(
        IAudioCaptureSession audio,
        WakeWordOptions options,
        CancellationToken cancellationToken);
}
```

Delete the old parameterless-audio overload so the coordinator remains the only capture-session owner.

- [ ] **Step 4: Implement the native boundary and JSON parser.**

Add the provider package to `DeskPilot.Voice.Vosk`:

```xml
<PackageReference Include="Vosk" />
```

```csharp
using var model = new Model(modelPath);
using var recognizer = new VoskRecognizer(model, 16_000f, "[\"альфа\"]");
recognizer.SetWords(true);
var isFinal = recognizer.AcceptWaveform(buffer, buffer.Length);
var json = isFinal ? recognizer.Result() : recognizer.PartialResult();
```

The adapter parses final `result[].conf` values and returns the minimum word confidence for the matched phrase. Partial results may update diagnostics but must not activate unless they contain a confidence supported by the C# result payload. Do not log rejected text; log only provider ID, confidence, and result kind.

- [ ] **Step 5: Implement `VoskWakeWordProvider`.**

Create one recognizer per wake session, require `AudioFormat.Pcm16KhzMono`, feed every frame, compare phrase using trimmed ordinal ignore-case Russian text, ignore confidence below the configured `0.65..0.90` threshold, and dispose the recognizer on success, cancellation, capture failure, and provider failure.

Add an opt-in smoke test controlled by `DESKPILOT_VOSK_SMOKE_MODEL` and `DESKPILOT_VOSK_SMOKE_AUDIO`; if either variable is absent, the test returns without touching the network or repository.

- [ ] **Step 6: Run the Task 3 gate.**

Run:

```powershell
dotnet test .\tests\DeskPilot.Voice.Tests\DeskPilot.Voice.Tests.csproj --filter "FullyQualifiedName~VoskWakeWordProviderTests"
dotnet build .\src\DeskPilot.Voice.Vosk\DeskPilot.Voice.Vosk.csproj -c Release --no-restore
```

Expected: all Vosk boundary tests pass and the provider project builds without warnings.

- [ ] **Step 7: Commit Task 3.**

```powershell
git add Directory.Packages.props src/DeskPilot.Voice.Abstractions src/DeskPilot.Voice.Vosk tests/DeskPilot.Voice.Tests
git commit -m "feat: add alpha wake word provider"
```

## Task 4: Add in-memory VAD, Whisper transcription, and local signals

**Files:**

- Modify: `src/DeskPilot.Voice.Abstractions/Audio/VoiceAudioContracts.cs`
- Modify: `src/DeskPilot.Voice.Abstractions/VoiceContracts.cs`
- Create: `src/DeskPilot.Voice.AudioCapture/EnergyVoiceActivityDetector.cs`
- Modify: `src/DeskPilot.Voice.WhisperCpp/DeskPilot.Voice.WhisperCpp.csproj`
- Create: `src/DeskPilot.Voice.WhisperCpp/IWhisperClient.cs`
- Create: `src/DeskPilot.Voice.WhisperCpp/WhisperNetClient.cs`
- Create: `src/DeskPilot.Voice.WhisperCpp/WhisperCppSpeechToTextProvider.cs`
- Create: `src/DeskPilot.Voice.WhisperCpp/WhisperServiceCollectionExtensions.cs`
- Create: `src/DeskPilot.Desktop/Services/LocalVoiceSignalService.cs`
- Create: `tests/DeskPilot.Voice.Tests/EnergyVoiceActivityDetectorTests.cs`
- Create: `tests/DeskPilot.Voice.Tests/WhisperCppSpeechToTextProviderTests.cs`

**Interfaces:**

- Consumes: normalized `AudioFrame` values, `VoiceActivityOptions.Default`, active `CommandWhisper` model path, and language `ru`.
- Produces: `CapturedCommandAudio`, `VoiceActivityResult`, `ISpeechToTextProvider`, and `IVoiceSignalService`.

- [ ] **Step 1: Write failing VAD tests with synthetic PCM and Whisper tests with a fake native client.**

```csharp
[Theory]
[InlineData(200, 900, false)]
[InlineData(300, 900, true)]
public async Task CaptureAsync_EnforcesMinimumSpeech(int speechMs, int trailingSilenceMs, bool expected)
{
    await using var audio = FakeCaptureSession.FromPcm(
        TestAudio.Silence(120),
        TestAudio.Speech(speechMs, amplitude: 0.25),
        TestAudio.Silence(trailingSilenceMs));
    var detector = new EnergyVoiceActivityDetector();

    var result = await detector.CaptureAsync(audio, VoiceActivityOptions.Default, CancellationToken.None);

    result.SpeechDetected.Should().Be(expected);
    result.Audio?.Duration.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(10));
}

[Fact]
public async Task RecognizeAsync_ForcesRussianAndConcatenatesFinalSegments()
{
    var client = Substitute.For<IWhisperClient>();
    client.ProcessAsync(Arg.Any<float[]>(), "ru", Arg.Any<CancellationToken>())
        .Returns(AsyncSegments.Of(new(" включи", 0.92), new(" музыку", 0.88)));
    var provider = new WhisperCppSpeechToTextProvider(_ => client);

    var result = await provider.RecognizeAsync(TestAudio.CommandStream(), new("ru", 0.70), CancellationToken.None);

    result.Text.Should().Be("включи музыку");
    result.Confidence.Should().BeApproximately(0.90, 0.001);
}
```

- [ ] **Step 2: Run the focused tests and confirm VAD/Whisper types are missing.**

Run:

```powershell
dotnet test .\tests\DeskPilot.Voice.Tests\DeskPilot.Voice.Tests.csproj --filter "FullyQualifiedName~EnergyVoiceActivityDetectorTests|FullyQualifiedName~WhisperCppSpeechToTextProviderTests"
```

Expected: compilation failures naming the detector or Whisper client boundary.

- [ ] **Step 3: Replace the stream-only VAD result with bounded captured audio.**

```csharp
public sealed record CapturedCommandAudio(ReadOnlyMemory<byte> Pcm16, AudioFormat Format, TimeSpan Duration)
{
    public Stream OpenRead() => new MemoryStream(Pcm16.ToArray(), writable: false);
}

public sealed record VoiceActivityResult(bool SpeechDetected, TimeSpan Duration, CapturedCommandAudio? Audio);

public interface IVoiceActivityDetector
{
    Task<VoiceActivityResult> CaptureAsync(
        IAudioCaptureSession input,
        VoiceActivityOptions options,
        CancellationToken cancellationToken);
}
```

Remove the obsolete `IAudioInputStream` contract after all consumers use capture sessions.

- [ ] **Step 4: Implement energy VAD with the approved timing limits.**

Process 20 ms frames, calculate RMS from little-endian PCM16, and use a configurable threshold derived from the microphone sensitivity. Discard leading silence, start the in-memory buffer at the first speech frame, require 250 ms total speech, finish after 900 ms trailing silence, and hard-stop at ten seconds. Clear pooled buffers in `finally`; return `SpeechDetected=false` for noise shorter than the minimum.

- [ ] **Step 5: Implement the Whisper.net 1.9.1 CPU adapter.**

Add both packages to `DeskPilot.Voice.WhisperCpp`:

```xml
<PackageReference Include="Whisper.net" />
<PackageReference Include="Whisper.net.Runtime" />
```

```csharp
using var factory = WhisperFactory.FromPath(modelPath);
await using var processor = factory.CreateBuilder()
    .WithLanguage("ru")
    .Build();

await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
{
    yield return new WhisperSegment(segment.Text, segment.Probability);
}
```

Convert PCM16 to `float[]` by dividing each signed sample by `32768f`. Reject non-16 kHz mono input, blank final text, missing model, and confidence below `SpeechRecognitionOptions.MinimumConfidence` with typed provider exceptions. Dispose processor asynchronously and factory deterministically.

Add an opt-in smoke test controlled by `DESKPILOT_WHISPER_SMOKE_MODEL` and `DESKPILOT_WHISPER_SMOKE_AUDIO`; absence means skip without download.

- [ ] **Step 6: Add local signal service without user audio files.**

```csharp
public enum VoiceSignal { Ready, Success, Failure }

public interface IVoiceSignalService
{
    Task PlayAsync(VoiceSignal signal, CancellationToken cancellationToken);
}
```

`LocalVoiceSignalService` maps `Ready`, `Success`, and `Failure` to short local system tones. It never blocks the WPF dispatcher, records audio, or opens a file supplied by the user.

- [ ] **Step 7: Run the Task 4 gate.**

Run:

```powershell
dotnet test .\tests\DeskPilot.Voice.Tests\DeskPilot.Voice.Tests.csproj --filter "FullyQualifiedName~EnergyVoiceActivityDetectorTests|FullyQualifiedName~WhisperCppSpeechToTextProviderTests"
dotnet build .\src\DeskPilot.Voice.WhisperCpp\DeskPilot.Voice.WhisperCpp.csproj -c Release --no-restore
```

Expected: synthetic PCM and fake-native tests pass; Whisper project builds without warnings.

- [ ] **Step 8: Commit Task 4.**

```powershell
git add Directory.Packages.props src/DeskPilot.Voice.Abstractions src/DeskPilot.Voice.AudioCapture src/DeskPilot.Voice.WhisperCpp src/DeskPilot.Desktop/Services tests/DeskPilot.Voice.Tests
git commit -m "feat: add local command transcription"
```

## Task 5: Integrate the state machine, WPF voice surface, release assets, and milestone gate

**Files:**

- Create: `src/DeskPilot.Application/Voice/VoicePipelineCoordinator.cs`
- Create: `src/DeskPilot.Application/Voice/VoicePipelineStateStore.cs`
- Create: `src/DeskPilot.Application/Voice/VoiceApplicationServiceCollectionExtensions.cs`
- Create: `src/DeskPilot.Desktop/ViewModels/VoiceControlViewModel.cs`
- Modify: `src/DeskPilot.Desktop/ViewModels/MainViewModel.cs`
- Modify: `src/DeskPilot.Desktop/MainWindow.xaml`
- Modify: `src/DeskPilot.Desktop/App.xaml.cs`
- Create: `scripts/voice-model-assets.ps1`
- Create: `tests/DeskPilot.Application.Tests/VoicePipelineCoordinatorTests.cs`
- Modify: `tests/DeskPilot.Application.Tests/DeskPilot.Application.Tests.csproj`
- Create: `tests/DeskPilot.Desktop.Tests/VoiceControlViewModelTests.cs`
- Modify: `docs/requirements.md`
- Modify: `docs/architecture.md`
- Modify: `docs/roadmap.md`
- Modify: `docs/voice-pipeline.md`
- Modify: `docs/progress.md`
- Modify: `docs/security.md`

**Interfaces:**

- Consumes: model manager/store, voice settings, input devices, capture sessions, Vosk wake provider, VAD, Whisper provider, local signals, `TimeProvider`, and structured logging.
- Produces: one cancellable `VoicePipelineCoordinator`, observable `VoicePipelineSnapshot`, WPF voice controls, verified seed release assets, and the completed Milestone 2 documentation gate.

- [ ] **Step 1: Write failing coordinator tests for every state transition and resource failure.**

```csharp
[Fact]
public async Task RunCycleAsync_PublishesTextWithoutDispatchingACommand()
{
    var fixture = PipelineFixture.Create();
    fixture.Wake.Returns(new WakeWordDetectionResult("альфа", 0.93));
    fixture.Vad.Returns(new VoiceActivityResult(true, TimeSpan.FromSeconds(1), TestAudio.CapturedCommand()));
    fixture.Speech.Returns(new SpeechRecognitionResult("сделай громче", 0.91, true));

    await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

    fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.WaitingForWakeWord);
    fixture.State.Snapshot.LastRecognizedText.Should().Be("сделай громче");
    fixture.State.History.Should().ContainInOrder(
        VoiceAssistantState.WaitingForWakeWord,
        VoiceAssistantState.WakeWordDetected,
        VoiceAssistantState.ListeningForCommand,
        VoiceAssistantState.DetectingSpeechEnd,
        VoiceAssistantState.RecognizingCommand,
        VoiceAssistantState.Cooldown,
        VoiceAssistantState.WaitingForWakeWord);
}

[Fact]
public async Task ExplicitBluetoothMicrophoneReconnect_ResumesSameEndpoint()
{
    var fixture = PipelineFixture.Create(selectedEndpointId: "bt-mic");
    fixture.Devices.ResolveSequence(
        new(AudioInputResultCode.SelectedDeviceUnavailable, null),
        new(AudioInputResultCode.Success, new("bt-mic", "Bluetooth microphone", false, true)));

    await fixture.Coordinator.EnableAsync(CancellationToken.None);
    await fixture.Devices.PublishChangeAsync();

    fixture.Capture.ReceivedEndpointIds.Should().OnlyContain(id => id == "bt-mic");
}
```

The application test project must not reference `ICommandDispatcher` from the voice fixture. This proves the Milestone 2 pipeline cannot dispatch recognized text by construction.

- [ ] **Step 2: Run coordinator tests and confirm the application service is missing.**

Run:

```powershell
dotnet test .\tests\DeskPilot.Application.Tests\DeskPilot.Application.Tests.csproj --filter "FullyQualifiedName~VoicePipelineCoordinatorTests"
```

Expected: compilation failure naming `VoicePipelineCoordinator` or `VoicePipelineStateStore`.

Add `NSubstitute` and a direct project reference to `DeskPilot.Voice.Abstractions` to `DeskPilot.Application.Tests`; production `DeskPilot.Application` already references the abstraction project and must not reference NAudio, Vosk, Whisper.net, WPF, or Infrastructure.

- [ ] **Step 3: Implement the single-owner state machine.**

```csharp
public sealed record VoicePipelineSnapshot(
    VoiceAssistantState State,
    string? MicrophoneEndpointId,
    string? LastWakePhrase,
    double? LastWakeConfidence,
    string? LastRecognizedText,
    double? LastRecognitionConfidence,
    string? ErrorCode,
    string? SafeMessage);
```

`VoicePipelineCoordinator` owns exactly one run task and cancellation source. `EnableAsync` and `DisableAsync` are idempotent; model activation and microphone changes serialize through one `SemaphoreSlim`. One cycle performs:

1. Resolve healthy active Vosk and Whisper models and the persisted microphone.
2. Open the wake capture session and publish `WaitingForWakeWord`.
3. Wait for `альфа`, dispose wake capture, publish `WakeWordDetected`, and play `Ready`.
4. Open a fresh command capture, publish `ListeningForCommand`, then `DetectingSpeechEnd`.
5. Dispose command capture before Whisper work; publish `RecognizingCommand`.
6. Publish recognized text/confidence only to `VoicePipelineStateStore` and play success/failure.
7. Clear audio references, publish `Cooldown`, delay two seconds through `TimeProvider`, then repeat.

Missing explicit microphone enters `Error` with `microphone-unavailable`; device changes retry only the saved ID. Disabling from any state cancels and awaits the run task. Provider/model failures expose typed safe messages and never include audio or personal absolute paths.

- [ ] **Step 4: Write and implement WPF-independent ViewModel tests.**

```csharp
[Fact]
public async Task RefreshMicrophonesAsync_KeepsDisconnectedSavedBluetoothMicrophoneSelected()
{
    var settings = Substitute.For<IVoiceSettingsRepository>();
    settings.GetAsync(Arg.Any<CancellationToken>()).Returns(
        VoiceSettings.Default with { MicrophoneEndpointId = "bt-mic", MicrophoneFriendlyName = "Bluetooth microphone" });
    var devices = Substitute.For<IAudioInputDeviceService>();
    devices.GetActiveAsync(Arg.Any<CancellationToken>()).Returns([]);
    var viewModel = VoiceViewModelFixture.Create(settings, devices);

    await viewModel.RefreshMicrophonesAsync();

    viewModel.SelectedMicrophone!.EndpointId.Should().Be("bt-mic");
    viewModel.SelectedMicrophone.IsAvailable.Should().BeFalse();
    viewModel.StatusMessage.Should().Contain("Bluetooth");
}
```

`VoiceControlViewModel` exposes enable/disable, microphone refresh/selection, sensitivity `0.65..0.90`, current state, last text, active models, the nested `VoiceModelManagerViewModel`, and safe recovery text. All long-running commands are asynchronous and prevent re-entry.

- [ ] **Step 5: Add the WPF voice and Model Manager surface and register services.**

Add a separate `Голосовое управление` section below the existing manual audio contour with:

- enable toggle and current state;
- capture-device ComboBox and refresh button;
- Bluetooth disconnect/reconnect guidance;
- wake sensitivity slider and fixed phrase `альфа`;
- last recognized command text;
- active Vosk/Whisper model cards;
- buttons `Проверить обновления`, `Скачать/обновить`, `Отмена`, `Использовать`, and `Восстановить встроенную`;
- download progress and safe diagnostics.

Register Infrastructure model services, audio capture, Vosk, Whisper, coordinator, state store, the Application implementation of `IVoiceModelActivationGate`, local signals, and ViewModels in `App.CreateHost`. Start the coordinator only after the database and `SeedVoiceModelInitializer` complete. The activation gate cancels and disposes the active wake/capture/native session, holds the coordinator lock during the repository activation transaction, then resumes `WaitingForWakeWord` when its async lease is disposed. During shutdown, disable the coordinator before `StopAsync` and stay within the existing five-second host timeout.

- [ ] **Step 6: Add release model preparation and verification.**

`scripts/voice-model-assets.ps1` accepts explicit artifact URIs, expected SHA-256 values, output directory, and optional signing-key path. It downloads only during an explicit release command, verifies hashes, writes license notices, injects `wake-ru-0.22.zip` and `ggml-base.bin` into `assets\voice-models` under publish output, signs the UTF-8 catalog manifest when a key path is supplied, and fails if any expected asset is missing or oversized.

Add a verification-only mode used by CI/release that checks manifest schema/signature, SHA-256, Vosk archive structure, Whisper ggml header, license files, and that no seed model is tracked by Git.

- [ ] **Step 7: Run the complete fresh verification gate.**

Run:

```powershell
dotnet restore .\DeskPilot.sln
dotnet build .\DeskPilot.sln -c Release --no-restore
dotnet test .\DeskPilot.sln -c Release --no-build
dotnet format .\DeskPilot.sln --verify-no-changes --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\voice-model-assets.ps1 -VerifyOnly
git diff --check
git ls-files | Select-String -Pattern '(^|/)(models|runtime|logs|audio|publish|artifacts)/|\.(db|sqlite|wav|mp3|gguf|bin)$'
git grep -n -I -E 'private key|api key|password|secret' -- ':!docs/security.md'
```

Expected: restore/build/tests/format/model verification exit `0`; tracked runtime/model scan returns no matches; the secret scan contains no credential value. Then start the WPF application, verify microphone enumeration, select the connected Bluetooth microphone, disconnect/reconnect it, detect `альфа`, speak one Russian command, and confirm only recognized text is displayed.

- [ ] **Step 8: Update milestone documentation only after the full gate passes.**

Mark Milestone 2 complete in `docs/progress.md` and `docs/roadmap.md`; document the implemented pipeline, model update/restore controls, Bluetooth endpoint behavior, offline seed behavior, security boundaries, opt-in smoke variables, and the explicit limitation that command dispatch begins in Milestone 3.

- [ ] **Step 9: Commit Task 5 and push the feature branch.**

```powershell
git add src tests scripts assets/voice-models docs
git commit -m "feat: complete offline voice recognition milestone"
git push origin feature/wake-word-voice-recognition
```

After remote verification, create a pull request from `feature/wake-word-voice-recognition` into `develop`. Do not merge directly into `main`; the release path remains `develop` to `main` through a separate reviewed pull request.

## Completion Checklist

- [ ] Built-in Vosk small Russian and Whisper base models work offline from publish output.
- [ ] Optional multilingual Whisper small can be checked, downloaded, cancelled, verified, activated, rolled back, and removed from active use without corrupting the seed model.
- [ ] The selected Bluetooth/USB microphone persists and recovers only by the same endpoint ID.
- [ ] `альфа` activates one command capture; VAD enforces 250 ms, 900 ms, and 10 seconds.
- [ ] Whisper recognizes Russian locally and WPF displays text without resolving or dispatching it.
- [ ] Audio is never persisted, transmitted, or logged.
- [ ] Full restore/build/test/format/release-asset/security/public-repository gate passes.
- [ ] Documentation marks only Milestone 2 complete and routes the next feature from updated `develop`.
