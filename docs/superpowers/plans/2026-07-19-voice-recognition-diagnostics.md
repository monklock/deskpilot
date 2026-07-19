# Voice Recognition Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Показывать в UI фактический текст Whisper и его confidence даже при низкой уверенности, а при отсутствии речи или текста выводить `Команда не распознана`, не передавая такие результаты на исполнение.

**Architecture:** Whisper сохраняет безопасную диагностическую нагрузку в типизированном исключении низкой уверенности. `VoicePipelineCoordinator` переносит её в существующий `VoicePipelineSnapshot`, но завершает цикл до resolver/dispatcher; отсутствие речи публикуется отдельным стабильным кодом. `VoiceControlViewModel` форматирует диагностический результат для существующей строки `Результат`.

**Tech Stack:** .NET 10, C# 14, WPF, CommunityToolkit.Mvvm, xUnit, FluentAssertions, NSubstitute, Whisper.net.

## Global Constraints

- Этот срез диагностический: не менять VAD, пороги confidence, resolver, dispatcher и обработчики аудиокоманд.
- Низкоуверенный текст отображается локально, но никогда не передаётся в `IVoiceCommandExecutionService`.
- Распознанный текст не записывается в логи и не сохраняется на диск.
- Для непустого результата ниже порога UI показывает исходный текст и `Распознано, confidence: 0.42` с двумя знаками и точкой.
- Для отсутствия речи или текста UI показывает `Команда не распознана`.
- Существующие успешные исходы команд и безопасные сообщения об ошибках сохраняются.
- Работа остаётся в `feature/voice-controlled-audio`; итоговая интеграция идёт только через PR в `develop`.

---

## File Structure

- `src/DeskPilot.Voice.Abstractions/VoiceContracts.cs` — безопасная диагностическая нагрузка типизированной ошибки распознавания.
- `src/DeskPilot.Voice.WhisperCpp/WhisperCppSpeechToTextProvider.cs` — заполнение текста и confidence при результате ниже порога.
- `src/DeskPilot.Application/Voice/VoicePipelineCoordinator.cs` — публикация диагностического snapshot и результата при отсутствии речи.
- `src/DeskPilot.Desktop/ViewModels/VoiceControlViewModel.cs` — форматирование существующей строки `Результат`.
- `tests/DeskPilot.Voice.Tests/WhisperCppSpeechToTextProviderTests.cs` — контракт диагностической нагрузки Whisper.
- `tests/DeskPilot.Application.Tests/VoicePipelineCoordinatorTests.cs` — запрет исполнения и сохранение диагностики.
- `tests/DeskPilot.Desktop.Tests/VoiceControlViewModelTests.cs` — точные русские строки результата.
- `.diagnostics/VoiceInputProbe/` — временный исследовательский проект, удаляемый до итоговой проверки.

---

### Task 1: Preserve Low-Confidence Whisper Output

**Files:**
- Modify: `src/DeskPilot.Voice.Abstractions/VoiceContracts.cs:69-84`
- Modify: `src/DeskPilot.Voice.WhisperCpp/WhisperCppSpeechToTextProvider.cs:84-95`
- Test: `tests/DeskPilot.Voice.Tests/WhisperCppSpeechToTextProviderTests.cs:30-51`

**Interfaces:**
- Consumes: `SpeechRecognitionFailureCode.ConfidenceBelowThreshold`, `WhisperSegment.Text`, `WhisperSegment.Confidence`.
- Produces: `SpeechRecognitionException.RecognizedText : string?` and `SpeechRecognitionException.RecognitionConfidence : double?`.

- [ ] **Step 1: Extend the failing-path test with the diagnostic contract**

Replace its final assertion with:

```csharp
var assertion = await action.Should().ThrowAsync<SpeechRecognitionException>()
    .Where(exception => exception.Code == SpeechRecognitionFailureCode.ConfidenceBelowThreshold);
assertion.Which.RecognizedText.Should().Be("команда");
assertion.Which.RecognitionConfidence.Should().Be(0.69);
client.DisposeCount.Should().Be(1);
```

- [ ] **Step 2: Run the narrow test and verify RED**

```powershell
dotnet test .\tests\DeskPilot.Voice.Tests\DeskPilot.Voice.Tests.csproj --no-restore --filter "FullyQualifiedName~WhisperCppSpeechToTextProviderTests.RecognizeAsync_ConfidenceBelowThreshold_ThrowsTypedFailure"
```

Expected: compilation fails because both diagnostic properties are missing.

- [ ] **Step 3: Add optional diagnostic properties to the exception**

Add after `Code`:

```csharp
/// <summary>Gets the recognized text retained for local diagnostics.</summary>
public string? RecognizedText { get; init; }

/// <summary>Gets the recognition confidence retained for local diagnostics.</summary>
public double? RecognitionConfidence { get; init; }
```

- [ ] **Step 4: Populate diagnostics only for a low-confidence result**

Replace that throw with:

```csharp
throw new SpeechRecognitionException(
    SpeechRecognitionFailureCode.ConfidenceBelowThreshold,
    "Whisper recognition confidence is below the configured threshold.")
{
    RecognizedText = text,
    RecognitionConfidence = confidence,
};
```

- [ ] **Step 5: Run provider tests and verify GREEN**

```powershell
dotnet test .\tests\DeskPilot.Voice.Tests\DeskPilot.Voice.Tests.csproj --no-restore --filter "FullyQualifiedName~WhisperCppSpeechToTextProviderTests"
```

Expected: all provider tests pass, including exact text `команда` and confidence `0.69`.

- [ ] **Step 6: Commit the provider contract**

```powershell
git add src/DeskPilot.Voice.Abstractions/VoiceContracts.cs src/DeskPilot.Voice.WhisperCpp/WhisperCppSpeechToTextProvider.cs tests/DeskPilot.Voice.Tests/WhisperCppSpeechToTextProviderTests.cs
git commit -m "feat: preserve voice recognition diagnostics"
```

---

### Task 2: Publish Diagnostic Snapshots Without Executing Commands

**Files:**
- Modify: `src/DeskPilot.Application/Voice/VoicePipelineCoordinator.cs:156-160,344-348,455-466`
- Test: `tests/DeskPilot.Application.Tests/VoicePipelineCoordinatorTests.cs`

**Interfaces:**
- Consumes: diagnostic properties from Task 1 and existing `VoicePipelineSnapshot` fields.
- Produces: `speech-confidence-low` with text/confidence and `speech-not-detected` with `Команда не распознана`.

- [ ] **Step 1: Write the failing low-confidence coordinator test**

```csharp
[Fact]
public async Task RunSingleCycleAsync_LowConfidence_PublishesTextWithoutExecutingCommand()
{
    var fixture = PipelineFixture.Create();
    fixture.Speech.RecognizeAsync(
            Arg.Any<CapturedCommandAudio>(),
            Arg.Any<SpeechRecognitionOptions>(),
            Arg.Any<CancellationToken>())
        .Returns<SpeechRecognitionResult>(_ => throw new SpeechRecognitionException(
            SpeechRecognitionFailureCode.ConfidenceBelowThreshold,
            "safe failure")
        {
            RecognizedText = "сделай тише",
            RecognitionConfidence = 0.42,
        });

    await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

    fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.Error);
    fixture.State.Snapshot.ErrorCode.Should().Be("speech-confidence-low");
    fixture.State.Snapshot.LastRecognizedText.Should().Be("сделай тише");
    fixture.State.Snapshot.LastRecognitionConfidence.Should().Be(0.42);
    await fixture.Commands.DidNotReceive().ExecuteAsync(
        Arg.Any<string>(),
        Arg.Any<Action<VoiceCommandExecutionProgress>>(),
        Arg.Any<CancellationToken>());
}
```

Expose the existing substitute from `PipelineFixture`:

```csharp
public required ISpeechToTextProvider Speech { get; init; }
```

and assign it in `Create`:

```csharp
Speech = speech,
```

- [ ] **Step 2: Run the low-confidence test and verify RED**

```powershell
dotnet test .\tests\DeskPilot.Application.Tests\DeskPilot.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~VoicePipelineCoordinatorTests.RunSingleCycleAsync_LowConfidence_PublishesTextWithoutExecutingCommand"
```

Expected: `speech-confidence-low` is present, but recognized text and confidence are null.

- [ ] **Step 3: Publish exception diagnostics in the catch path**

Replace the generic `PublishError` call in the speech catch with:

```csharp
Publish(_state.Snapshot with
{
    State = VoiceAssistantState.Error,
    LastRecognizedText = exception.RecognizedText,
    LastRecognitionConfidence = exception.RecognitionConfidence,
    LastResolvedCommandId = null,
    LastIntentStatus = null,
    LastIntentConfidence = null,
    LastExecutionStatus = null,
    ErrorCode = ToSpeechErrorCode(exception.Code),
    SafeMessage = ToSpeechSafeMessage(exception.Code),
});
```

- [ ] **Step 4: Run the low-confidence test and verify GREEN**

Repeat Step 2. Expected: test passes and `IVoiceCommandExecutionService` has zero calls.

- [ ] **Step 5: Write the failing no-speech test**

```csharp
[Fact]
public async Task RunSingleCycleAsync_NoSpeech_PublishesNotRecognizedWithoutCallingWhisper()
{
    var fixture = PipelineFixture.Create();
    fixture.VoiceActivity.CaptureAsync(
            Arg.Any<IAudioCaptureSession>(),
            Arg.Any<VoiceActivityOptions>(),
            Arg.Any<CancellationToken>())
        .Returns(new VoiceActivityResult(false, TimeSpan.Zero, null));

    await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

    fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.WaitingForWakeWord);
    fixture.State.Snapshot.ErrorCode.Should().Be("speech-not-detected");
    fixture.State.Snapshot.SafeMessage.Should().Be("Команда не распознана");
    fixture.State.Snapshot.LastRecognizedText.Should().BeNull();
    fixture.State.Snapshot.LastRecognitionConfidence.Should().BeNull();
    await fixture.Speech.DidNotReceive().RecognizeAsync(
        Arg.Any<CapturedCommandAudio>(),
        Arg.Any<SpeechRecognitionOptions>(),
        Arg.Any<CancellationToken>());
}
```

- [ ] **Step 6: Run the no-speech test and verify RED**

```powershell
dotnet test .\tests\DeskPilot.Application.Tests\DeskPilot.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~VoicePipelineCoordinatorTests.RunSingleCycleAsync_NoSpeech_PublishesNotRecognizedWithoutCallingWhisper"
```

Expected: the final snapshot lacks the new code and message.

- [ ] **Step 7: Publish a stable no-speech result before cooldown**

```csharp
if (!activity.SpeechDetected || activity.Audio is null)
{
    Publish(_state.Snapshot with
    {
        LastRecognizedText = null,
        LastRecognitionConfidence = null,
        LastResolvedCommandId = null,
        LastIntentStatus = null,
        LastIntentConfidence = null,
        LastExecutionStatus = null,
        ErrorCode = "speech-not-detected",
        SafeMessage = "Команда не распознана",
    });
    await PlaySignalSafelyAsync(VoiceSignal.Failure, cancellationToken).ConfigureAwait(false);
    await PublishCooldownAsync(settings.Cooldown, cancellationToken).ConfigureAwait(false);
    return;
}
```

- [ ] **Step 8: Run all coordinator tests and verify GREEN**

```powershell
dotnet test .\tests\DeskPilot.Application.Tests\DeskPilot.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~VoicePipelineCoordinatorTests"
```

Expected: all coordinator tests pass and successful command flow is unchanged.

- [ ] **Step 9: Commit the coordinator behavior**

```powershell
git add src/DeskPilot.Application/Voice/VoicePipelineCoordinator.cs tests/DeskPilot.Application.Tests/VoicePipelineCoordinatorTests.cs
git commit -m "feat: publish voice recognition diagnostics"
```

---

### Task 3: Render Recognition Diagnostics in the Existing UI

**Files:**
- Modify: `src/DeskPilot.Desktop/ViewModels/VoiceControlViewModel.cs:1-9,321-340`
- Test: `tests/DeskPilot.Desktop.Tests/VoiceControlViewModelTests.cs`
- Delete: `.diagnostics/VoiceInputProbe/Program.cs`
- Delete: `.diagnostics/VoiceInputProbe/VoiceInputProbe.csproj`
- Delete generated local probe outputs: `.diagnostics/VoiceInputProbe/bin/`, `.diagnostics/VoiceInputProbe/obj/`

**Interfaces:**
- Consumes: `LastRecognizedText`, `LastRecognitionConfidence`, `ErrorCode` from Task 2.
- Produces: `Распознано, confidence: 0.42` and `Команда не распознана` in `LastCommandOutcome`.

- [ ] **Step 1: Write failing ViewModel projection tests**

```csharp
[Fact]
public async Task StateChange_LowConfidence_ShowsRecognizedTextAndConfidence()
{
    var fixture = VoiceViewModelFixture.Create(VoiceSettings.Default, []);
    await fixture.ViewModel.InitializeAsync();
    var snapshot = VoicePipelineSnapshot.Disabled with
    {
        State = VoiceAssistantState.Error,
        LastRecognizedText = "сделай тише",
        LastRecognitionConfidence = 0.42,
        ErrorCode = "speech-confidence-low",
        SafeMessage = "Команда распознана неуверенно. Повторите её.",
    };

    fixture.State.SnapshotChanged +=
        Raise.Event<EventHandler<VoicePipelineSnapshot>>(fixture.State, snapshot);

    fixture.ViewModel.LastRecognizedText.Should().Be("сделай тише");
    fixture.ViewModel.LastCommandOutcome.Should().Be("Распознано, confidence: 0.42");
}

[Theory]
[InlineData("speech-not-detected")]
[InlineData("speech-not-recognized")]
public async Task StateChange_NoRecognizedText_ShowsStableDiagnosticOutcome(string errorCode)
{
    var fixture = VoiceViewModelFixture.Create(VoiceSettings.Default, []);
    await fixture.ViewModel.InitializeAsync();
    var snapshot = VoicePipelineSnapshot.Disabled with
    {
        State = VoiceAssistantState.Error,
        ErrorCode = errorCode,
        SafeMessage = "Команда не распознана",
    };

    fixture.State.SnapshotChanged +=
        Raise.Event<EventHandler<VoicePipelineSnapshot>>(fixture.State, snapshot);

    fixture.ViewModel.LastRecognizedText.Should().Be("—");
    fixture.ViewModel.LastCommandOutcome.Should().Be("Команда не распознана");
}
```

- [ ] **Step 2: Run the ViewModel tests and verify RED**

```powershell
dotnet test .\tests\DeskPilot.Desktop.Tests\DeskPilot.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~VoiceControlViewModelTests.StateChange_LowConfidence_ShowsRecognizedTextAndConfidence|FullyQualifiedName~VoiceControlViewModelTests.StateChange_NoRecognizedText_ShowsStableDiagnosticOutcome"
```

Expected: `LastCommandOutcome` is `—`.

- [ ] **Step 3: Format diagnostic outcomes before command outcomes**

Add `using System.Globalization;`, then replace `ToCommandOutcome` with:

```csharp
private static string ToCommandOutcome(VoicePipelineSnapshot snapshot)
{
    if (snapshot.ErrorCode is "speech-not-detected" or "speech-not-recognized")
    {
        return "Команда не распознана";
    }

    if (snapshot.ErrorCode == "speech-confidence-low"
        && !string.IsNullOrWhiteSpace(snapshot.LastRecognizedText)
        && snapshot.LastRecognitionConfidence is double confidence)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Распознано, confidence: {confidence:F2}");
    }

    return snapshot.LastResolvedCommandId is null
        ? snapshot.LastIntentStatus switch
        {
            IntentResolutionStatus.NotFound => "Команда не найдена",
            IntentResolutionStatus.Ambiguous => "Команда неоднозначна",
            _ => "—",
        }
        : snapshot.LastExecutionStatus switch
        {
            CommandExecutionStatus.Succeeded => $"{snapshot.LastResolvedCommandId} — выполнено",
            CommandExecutionStatus.Rejected => $"{snapshot.LastResolvedCommandId} — отклонено",
            CommandExecutionStatus.Failed => $"{snapshot.LastResolvedCommandId} — ошибка",
            CommandExecutionStatus.NotFound => $"{snapshot.LastResolvedCommandId} — недоступно",
            _ => snapshot.LastResolvedCommandId,
        };
}
```

- [ ] **Step 4: Run all ViewModel tests and verify GREEN**

```powershell
dotnet test .\tests\DeskPilot.Desktop.Tests\DeskPilot.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~VoiceControlViewModelTests"
```

Expected: all tests pass; confidence has exactly two decimal places and a dot.

- [ ] **Step 5: Remove the temporary probe safely**

Delete `Program.cs` and `VoiceInputProbe.csproj` with `apply_patch`. Resolve and verify `bin` and `obj` are inside `E:\home\DeskPilot\.worktrees\voice-controlled-audio\.diagnostics\VoiceInputProbe`, then remove only those exact generated directories and empty parents. Expected: no `.diagnostics/` entry in `git status --short`.

- [ ] **Step 6: Stop only the known running development app processes**

```powershell
Get-Process -Id 55956,4264 -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,Path
```

If they are still the known DeskPilot runner and app, stop only those IDs before the full build.

- [ ] **Step 7: Format and run the complete repository gate**

```powershell
dotnet format .\DeskPilot.sln --no-restore
dotnet format .\DeskPilot.sln --verify-no-changes --no-restore
dotnet build .\DeskPilot.sln --configuration Release --no-restore
dotnet test .\DeskPilot.sln --configuration Release --no-build --no-restore
```

Expected: format exits 0, build has 0 errors, all tests pass.

- [ ] **Step 8: Run repository safety checks**

```powershell
git status --short
git diff --check
git ls-files | Select-String -Pattern '(^|/)(models|logs|prompts|runtime)(/|$)|\.ggml$|\.bin$|\.log$|\.key$'
```

Expected: no whitespace errors and no local models, logs, prompts, credentials, probe files, or runtime data are tracked.

- [ ] **Step 9: Commit the UI diagnostics and reviewed formatting**

Stage the intended diagnostic files plus reviewed Milestone 3 source/doc changes, never runtime data, then:

```powershell
git commit -m "feat: show recognized voice text in UI"
```

- [ ] **Step 10: Relaunch DeskPilot for manual verification**

Start the Desktop project from this worktree. Say `Альфа`, then a command. Confirm that low-confidence text appears in `Последняя команда`, `Результат` shows `Распознано, confidence: N.NN`, no-speech shows `Команда не распознана`, and a low-confidence result does not change volume or mute state. Stop after launch and request live confirmation; do not declare Milestone 3 complete before it.
