# Manual Audio Control Implementation Plan

**Goal:** Deliver the complete manual Windows audio-control vertical slice: output devices, persistent speakers/headphones preferences, volume, mute, endpoint switching, and a WPF control surface.

**Architecture:** Keep AudioControl contracts independent of COM and NAudio. Infrastructure provides Windows Core Audio adapters and SQLite persistence; the module validates and routes actions; WPF dispatches mutations through registered commands and reads display state through the contracts.

**Tech stack:** .NET 10, WPF, CommunityToolkit.Mvvm, EF Core SQLite, NAudio.Wasapi 2.2.1, Windows Core Audio, xUnit, FluentAssertions, NSubstitute.

## Global Constraints

- Target Windows 10/11 x64 and `net10.0` / `net10.0-windows` project boundaries.
- Keep COM and NAudio types inside Infrastructure.
- Use stable Windows endpoint IDs for persistence; friendly names are display-only.
- Do not start PowerShell, CMD, or a helper executable.
- Convert expected device and COM failures into typed operation results and structured logs.
- Write an executable test before every production behavior change.
- Do not start Milestone 2 after this task.

---

## File Structure

| Path | Responsibility |
| --- | --- |
| `src/DeskPilot.Modules.AudioControl/AudioContracts.cs` | Platform-neutral audio models and service interfaces. |
| `src/DeskPilot.Modules.AudioControl/AudioCommandHandlers.cs` | Validates dispatcher arguments and invokes audio services. |
| `src/DeskPilot.Modules.AudioControl/AudioControlModule.cs` | Module metadata and command-handler registration. |
| `src/DeskPilot.Infrastructure/WindowsAudio/*` | NAudio Core Audio access, endpoint switching, and settings fallback. |
| `src/DeskPilot.Infrastructure/Preferences/*` | SQLite repository for preferred devices. |
| `src/DeskPilot.Infrastructure/Data/*` | Preferred-device entity and migration. |
| `src/DeskPilot.Desktop/ViewModels/AudioControlViewModel.cs` | UI state and asynchronous commands. |
| `src/DeskPilot.Desktop/MainWindow.xaml` | Manual audio controls. |
| `tests/DeskPilot.Modules.Tests/AudioControlTests.cs` | Contract, validation, and command-handler tests. |
| `tests/DeskPilot.Infrastructure.Tests/AudioPreferenceRepositoryTests.cs` | SQLite persistence and migration tests. |
| `tests/DeskPilot.Desktop.Tests/AudioControlViewModelTests.cs` | WPF-independent ViewModel tests with substitutes. |

## Task 1: Add contracts and command identifiers

**Files:**

- Create: `src/DeskPilot.Modules.AudioControl/AudioContracts.cs`
- Create: `src/DeskPilot.Modules.AudioControl/AudioCommandHandlers.cs`
- Create: `tests/DeskPilot.Modules.Tests/AudioControlTests.cs`
- Modify: `tests/DeskPilot.Modules.Tests/DeskPilot.Modules.Tests.csproj`

**Consumes:** `CommandRequest`, `CommandExecutionResult`, and `ICommandHandler`.

**Produces:** `IAudioVolumeService`, `IAudioOutputDeviceService`, `IAudioPreferredDeviceService`, `AudioOutputDevice`, `AudioVolumeState`, `AudioOperationResult`, and handlers for `audio.set-volume`, `audio.change-volume`, `audio.set-mute`, `audio.toggle-mute`, `audio.set-default-device`, and `audio.save-preferred-device`.

- [ ] **Step 1: Write failing tests for volume validation and device-command routing.**

```csharp
[Fact]
public async Task SetVolumeHandler_RejectsPercentageOutsideInclusiveRange()
{
    var service = Substitute.For<IAudioVolumeService>();
    var handler = new SetVolumeCommandHandler(service);

    var result = await handler.HandleAsync(
        new CommandRequest(CommandId.From("audio.set-volume"), new Dictionary<string, string> { ["percentage"] = "101" }),
        CancellationToken.None);

    result.Status.Should().Be(CommandExecutionStatus.Rejected);
    await service.DidNotReceive().SetVolumeAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Run the test and confirm it fails because the contract and handler do not exist.**

Run: `dotnet test .\tests\DeskPilot.Modules.Tests\DeskPilot.Modules.Tests.csproj --filter FullyQualifiedName~SetVolumeHandler_RejectsPercentageOutsideInclusiveRange`

Expected: compilation failure naming `IAudioVolumeService` or `SetVolumeCommandHandler`.

- [ ] **Step 3: Add immutable contracts and handlers.**

```csharp
public sealed record AudioVolumeState(string EndpointId, int Percentage, bool IsMuted);
public sealed record AudioOutputDevice(string EndpointId, string FriendlyName, bool IsAvailable, bool IsDefault);
public sealed record AudioOperationResult(bool IsSuccess, string? ErrorCode = null, string? Message = null);
public enum AudioDeviceSlot { Speakers, Headphones }
public enum AudioDeviceRole { Multimedia }
public sealed record AudioDeviceSwitchRequest(string EndpointId, AudioDeviceRole Role);
public sealed record AudioDeviceSwitchResult(bool IsSuccess, string? ErrorCode = null, string? Message = null);

public interface IAudioVolumeService
{
    Task<AudioVolumeState> GetStateAsync(CancellationToken cancellationToken);
    Task<AudioOperationResult> SetVolumeAsync(int percentage, CancellationToken cancellationToken);
    Task<AudioOperationResult> ChangeVolumeAsync(int deltaPercentage, CancellationToken cancellationToken);
    Task<AudioOperationResult> SetMuteAsync(bool muted, CancellationToken cancellationToken);
    Task<AudioOperationResult> ToggleMuteAsync(CancellationToken cancellationToken);
}

public interface IAudioOutputDeviceService
{
    Task<IReadOnlyCollection<AudioOutputDevice>> GetDevicesAsync(CancellationToken cancellationToken);
    Task<AudioOutputDevice?> GetDefaultDeviceAsync(AudioDeviceRole role, CancellationToken cancellationToken);
    Task<AudioDeviceSwitchResult> SetDefaultDeviceAsync(AudioDeviceSwitchRequest request, CancellationToken cancellationToken);
}

public interface IAudioPreferredDeviceService
{
    Task<AudioOutputDevice?> GetAsync(AudioDeviceSlot slot, CancellationToken cancellationToken);
    Task SaveAsync(AudioDeviceSlot slot, AudioOutputDevice device, CancellationToken cancellationToken);
}
```

Implement each handler with explicit argument parsing: `percentage` accepts only integers in `0..100`, `delta` only integers in `-100..100`, `muted` only `true` or `false`, `endpointId` a non-empty string, and `slot` only `Speakers` or `Headphones`. Return `Rejected` for malformed input, `Failed` for an unsuccessful operation result, and `Succeeded` only after the service succeeds.

- [ ] **Step 4: Run the complete module test project.**

Run: `dotnet test .\tests\DeskPilot.Modules.Tests\DeskPilot.Modules.Tests.csproj`

Expected: all existing and new tests pass.

## Task 2: Persist preferred endpoints by endpoint ID

**Files:**

- Create: `src/DeskPilot.Infrastructure/Preferences/SqliteAudioPreferredDeviceService.cs`
- Modify: `src/DeskPilot.Infrastructure/Data/DeskPilotDbContext.cs`
- Create: `src/DeskPilot.Infrastructure/Data/Migrations/202607180001_AddAudioDevicePreferences.cs`
- Modify: `src/DeskPilot.Infrastructure/InfrastructureServiceCollectionExtensions.cs`
- Create: `tests/DeskPilot.Infrastructure.Tests/AudioPreferenceRepositoryTests.cs`

**Consumes:** `IAudioPreferredDeviceService`, `AudioDeviceSlot`, and `IDbContextFactory<DeskPilotDbContext>`.

**Produces:** durable `Speakers` and `Headphones` preferences, with endpoint ID, friendly name, and one record per slot.

- [ ] **Step 1: Write the failing SQLite round-trip test.**

```csharp
[Fact]
public async Task SaveAsync_ReplacesThePreferenceForTheSameSlot()
{
    await using var context = CreateMigratedContext();
    var service = new SqliteAudioPreferredDeviceService(new TestDbContextFactory(context));

    await service.SaveAsync(AudioDeviceSlot.Speakers, new AudioOutputDevice("endpoint-b", "Speakers", true, false), CancellationToken.None);

    (await service.GetAsync(AudioDeviceSlot.Speakers, CancellationToken.None))!.EndpointId.Should().Be("endpoint-b");
}
```

- [ ] **Step 2: Run the test and confirm it fails because the preference service does not exist.**

Run: `dotnet test .\tests\DeskPilot.Infrastructure.Tests\DeskPilot.Infrastructure.Tests.csproj --filter FullyQualifiedName~SaveAsync_ReplacesThePreferenceForTheSameSlot`

Expected: compilation failure naming `SqliteAudioPreferredDeviceService`.

- [ ] **Step 3: Add the entity, model configuration, migration, and repository.**

```csharp
public sealed class AudioDevicePreferenceEntity
{
    public required string Slot { get; set; }
    public required string EndpointId { get; set; }
    public required string FriendlyName { get; set; }
}
```

Map `Slot` as the key and enforce maximum lengths of 32, 1024, and 512 characters. Use a short-lived context from `IDbContextFactory`, upsert by slot, and call `SaveChangesAsync`. Register the repository as a singleton service that depends on the factory.

- [ ] **Step 4: Extend migration coverage and run infrastructure tests.**

Run: `dotnet test .\tests\DeskPilot.Infrastructure.Tests\DeskPilot.Infrastructure.Tests.csproj`

Expected: the migrated database contains `AudioDevicePreferences`; preferences survive a new context instance.

## Task 3: Implement Windows Core Audio and safe default-device switching

**Files:**

- Modify: `Directory.Packages.props`
- Modify: `src/DeskPilot.Infrastructure/DeskPilot.Infrastructure.csproj`
- Create: `src/DeskPilot.Infrastructure/Audio/WindowsCoreAudioService.cs`
- Create: `src/DeskPilot.Infrastructure/Audio/PolicyConfigAudioEndpointSwitcher.cs`
- Create: `src/DeskPilot.Infrastructure/Audio/SystemSoundSettingsLauncher.cs`
- Modify: `src/DeskPilot.Infrastructure/InfrastructureServiceCollectionExtensions.cs`
- Create: `tests/DeskPilot.Infrastructure.Tests/AudioOperationMappingTests.cs`

**Consumes:** NAudio `MMDeviceEnumerator`, `DataFlow.Render`, `Role.Multimedia`, `AudioEndpointVolume`, `IAudioVolumeService`, and `IAudioOutputDeviceService`.

**Produces:** active render-device enumeration, default-device volume and mute control, change notifications, runtime-checked endpoint switching, and a `ms-settings:sound` fallback.

- [ ] **Step 1: Write failing tests for clamping and unavailable endpoint result mapping.**

```csharp
[Fact]
public async Task ChangeVolumeAsync_ClampsTheTargetToOneHundred()
{
    var client = new RecordingCoreAudioClient(new AudioVolumeState("default", 95, false));
    var service = new WindowsCoreAudioService(client, NullLogger<WindowsCoreAudioService>.Instance);

    await service.ChangeVolumeAsync(20, CancellationToken.None);

    client.LastSetPercentage.Should().Be(100);
}
```

- [ ] **Step 2: Run the test and confirm it fails because the adapter boundary does not exist.**

Run: `dotnet test .\tests\DeskPilot.Infrastructure.Tests\DeskPilot.Infrastructure.Tests.csproj --filter FullyQualifiedName~ChangeVolumeAsync_ClampsTheTargetToOneHundred`

Expected: compilation failure naming `WindowsCoreAudioService`.

- [ ] **Step 3: Add NAudio and implement the adapter behind a testable native-client boundary.**

```xml
<PackageVersion Include="NAudio.Wasapi" Version="2.2.1" />
```

```csharp
using var enumerator = new MMDeviceEnumerator();
using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
var percentage = (int)Math.Round(endpoint.AudioEndpointVolume.MasterVolumeLevelScalar * 100, MidpointRounding.AwayFromZero);
```

Enumerate only render endpoints, retain no COM object outside Infrastructure, detach notification callbacks during disposal, and map `COMException`, missing endpoints, and inactive endpoints to unsuccessful `AudioOperationResult` values. `PolicyConfigAudioEndpointSwitcher` must set all three Windows roles (`Console`, `Multimedia`, `Communications`) for the selected endpoint and return an unsupported result when activation fails. The fallback launcher must use `ProcessStartInfo { FileName = "ms-settings:sound", UseShellExecute = true }` only after an explicit unsuccessful switching result.

- [ ] **Step 4: Run infrastructure tests.**

Run: `dotnet test .\tests\DeskPilot.Infrastructure.Tests\DeskPilot.Infrastructure.Tests.csproj`

Expected: tests pass without changing the developer's default Windows device.

## Task 4: Register the module and connect the desktop UI

**Files:**

- Create: `src/DeskPilot.Modules.AudioControl/AudioControlModule.cs`
- Modify: `src/DeskPilot.Desktop/App.xaml.cs`
- Create: `src/DeskPilot.Desktop/ViewModels/AudioControlViewModel.cs`
- Modify: `src/DeskPilot.Desktop/ViewModels/MainViewModel.cs`
- Modify: `src/DeskPilot.Desktop/MainWindow.xaml`
- Modify: `src/DeskPilot.Desktop/MainWindow.xaml.cs`
- Create: `tests/DeskPilot.Desktop.Tests/DeskPilot.Desktop.Tests.csproj`
- Create: `tests/DeskPilot.Desktop.Tests/AudioControlViewModelTests.cs`
- Modify: `DeskPilot.sln`

**Consumes:** the audio contracts, `ICommandDispatcher`, `ModuleCatalog`, and the Windows adapter registrations.

**Produces:** an initialized AudioControl module and UI controls for refresh, volume, mute, speakers, headphones, and settings fallback.

- [ ] **Step 1: Write a failing ViewModel test for unavailable preferred devices.**

```csharp
[Fact]
public async Task UsePreferredDeviceAsync_ShowsSafeStatusWhenHeadphonesAreUnavailable()
{
    var output = Substitute.For<IAudioOutputDeviceService>();
    var preferences = Substitute.For<IAudioPreferredDeviceService>();
    var dispatcher = Substitute.For<ICommandDispatcher>();
    preferences.GetAsync(AudioDeviceSlot.Headphones, Arg.Any<CancellationToken>())
        .Returns(new AudioOutputDevice("headphones", "Headphones", false, false));
    dispatcher.DispatchAsync(Arg.Any<CommandRequest>(), Arg.Any<CancellationToken>())
        .Returns(new CommandExecutionResult(CommandId.From("audio.set-default-device"), CommandExecutionStatus.Failed, "Headphones are unavailable."));
    var viewModel = new AudioControlViewModel(output, Substitute.For<IAudioVolumeService>(), preferences, dispatcher);

    await viewModel.UseHeadphonesCommand.ExecuteAsync(null);

    viewModel.StatusMessage.Should().Be("Headphones are unavailable.");
}
```

- [ ] **Step 2: Run the test and confirm it fails because `AudioControlViewModel` does not exist.**

Run: `dotnet test .\tests\DeskPilot.Desktop.Tests\DeskPilot.Desktop.Tests.csproj --filter FullyQualifiedName~UsePreferredDeviceAsync_ShowsSafeStatusWhenHeadphonesAreUnavailable`

Expected: compilation failure naming `AudioControlViewModel`.

- [ ] **Step 3: Register the module and implement the ViewModel and XAML.**

```csharp
var moduleCatalog = new ModuleCatalog([new AudioControlModule()]);
moduleCatalog.RegisterServices(services);
services.AddSingleton(moduleCatalog);
services.AddSingleton<AudioControlViewModel>();
```

The ViewModel constructor receives `IAudioOutputDeviceService`, `IAudioVolumeService`, `IAudioPreferredDeviceService`, and `ICommandDispatcher`. It exposes `ObservableCollection<AudioOutputDevice> OutputDevices`, `VolumePercentage`, `IsMuted`, `DefaultDeviceName`, `StatusMessage`, and asynchronous commands for refresh, set volume, increase/decrease volume, toggle mute, save each preferred slot, and activate each preferred slot. Route every mutation through `ICommandDispatcher`; use the three services only for state queries, then refresh state after every successful result. Marshal adapter notifications through `Application.Current.Dispatcher`. Bind the XAML controls with `UpdateSourceTrigger=PropertyChanged` and disable a preferred-device action when its saved endpoint is unavailable.

- [ ] **Step 4: Run desktop and solution tests.**

Run: `dotnet test .\tests\DeskPilot.Desktop.Tests\DeskPilot.Desktop.Tests.csproj`

Run: `dotnet test .\DeskPilot.sln --configuration Release --no-restore`

Expected: ViewModel and full-solution tests pass.

## Task 5: Complete the milestone and verify the release-quality gate

**Files:**

- Modify: `docs/progress.md`
- Modify: `docs/requirements.md`
- Modify: `docs/architecture.md`
- Modify: `docs/roadmap.md`

**Consumes:** all completed manual-audio components and test evidence.

**Produces:** an accurate project status with Task 1.1 complete and Milestone 2 as the next task.

- [ ] **Step 1: Update documentation only after all executable checks pass.**

Mark the Task 1.1 Definition of Done as complete; record the manual-audio behavior, the Windows-only adapter boundary, and the known limitation that automatic Bluetooth reconnection remains out of scope.

- [ ] **Step 2: Run the complete verification set.**

Run: `dotnet restore .\DeskPilot.sln`

Run: `dotnet build .\DeskPilot.sln --configuration Release --no-restore`

Run: `dotnet test .\DeskPilot.sln --configuration Release --no-restore`

Run: `dotnet format .\DeskPilot.sln --verify-no-changes --no-restore`

Run: `git diff --check`

Expected: restore succeeds, build reports zero errors, all tests pass, formatting has no changes, and Git reports no whitespace errors.

- [ ] **Step 3: Run the public-repository safety check.**

Run: `git ls-files | Select-String -Pattern '(^|/)(logs|models|runtime|audio|publish|artifacts)/|\\.(db|db-shm|db-wal|wav|mp3|gguf|bin)$'`

Run: `git grep -n -I -e 'sk-[A-Za-z0-9]' -e 'C:\\Users\\' -- .`

Expected: no runtime artifacts, secrets, or personal absolute paths are tracked.
