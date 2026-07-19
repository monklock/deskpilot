# Voice-Controlled Audio Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve locally recognized Russian phrases into a finite set of registered audio commands and execute them safely through the existing dispatcher.

**Architecture:** Modules publish deterministic phrase descriptions, `DeskPilot.Application` owns normalization and exact/fuzzy resolution, and a single execution service dispatches only catalog-backed requests. `VoicePipelineCoordinator` remains the lifecycle owner and projects safe resolution/execution metadata to WPF without logging recognized text or arguments.

**Tech Stack:** .NET 10, C# 14, WPF, CommunityToolkit.Mvvm 8.4.0, Generic Host/DI, xUnit, FluentAssertions, NSubstitute, existing Windows Core Audio adapters.

## Global Constraints

- Target Windows 10/11 x64 and preserve existing `net10.0` / `net10.0-windows` project boundaries.
- Work only on `feature/voice-controlled-audio`, created from updated `develop` after Milestone 2 PR #4.
- Keep Core and Application independent of WPF, SQLite, NAudio, Vosk, Whisper, and Windows COM.
- Resolve only statically registered commands published by enabled modules.
- Run exact resolution before fuzzy resolution; fuzzy threshold is `0.86` and ambiguity margin is `0.08`.
- Short louder/quieter phrases change volume by exactly `+10` / `-10` percentage points.
- Explicit volume values are integers from `0` through `100`, expressed as digits or Russian cardinal words.
- Never guess a missing endpoint, unavailable endpoint, numeric value, command ID, or command argument.
- Keep command audio in memory and never log recognized text, endpoint IDs, arguments, model paths, credentials, or audio data.
- Play success only after `CommandExecutionStatus.Succeeded`; all non-success outcomes use failure feedback and cooldown recovery.
- Do not add applications, command groups, shutdown, confirmation, LLM/API providers, external plugins, or user-defined phrases.
- Write a focused failing test before each production behavior and commit after every task gate.

---

## File Structure

### Create

- `src/DeskPilot.Modules.Abstractions/Commands/ICommandDescriptionProvider.cs` — module-owned command-description boundary.
- `src/DeskPilot.Application/Commands/CommandCatalog.cs` — validates descriptions against registered handlers.
- `src/DeskPilot.Application/Intents/CommandTextNormalizer.cs` — deterministic Russian normalization.
- `src/DeskPilot.Application/Intents/RussianVolumeNumberParser.cs` — strict `0..100` parser.
- `src/DeskPilot.Application/Intents/PhrasePatternMatcher.cs` — literal and percentage-template matching.
- `src/DeskPilot.Application/Intents/ExactPhraseIntentResolver.cs` — exact resolver.
- `src/DeskPilot.Application/Intents/FuzzyPhraseIntentResolver.cs` — bounded Damerau-Levenshtein resolver.
- `src/DeskPilot.Application/Intents/CompositeIntentResolver.cs` — exact-before-fuzzy ordering.
- `src/DeskPilot.Application/Voice/VoiceCommandExecutionService.cs` — resolve/progress/dispatch boundary.
- `src/DeskPilot.Modules.AudioControl/AudioCommandDescriptions.cs` — finite Russian phrase catalog.
- `tests/DeskPilot.Application.Tests/CommandCatalogTests.cs` — catalog validation.
- `tests/DeskPilot.Application.Tests/IntentResolverTests.cs` — normalization and resolver coverage.
- `tests/DeskPilot.Application.Tests/VoiceCommandExecutionServiceTests.cs` — dispatch safety.
- `docs/voice-commands.md` — supported commands and safety behavior.

### Modify

- `src/DeskPilot.Core/Commands/CommandContracts.cs`
- `src/DeskPilot.Modules.AudioControl/AudioControlModule.cs`
- `src/DeskPilot.Modules.AudioControl/AudioCommandHandlers.cs`
- `src/DeskPilot.Application/Voice/VoiceApplicationServiceCollectionExtensions.cs`
- `src/DeskPilot.Application/Voice/VoicePipelineCoordinator.cs`
- `src/DeskPilot.Application/Voice/VoicePipelineStateStore.cs`
- `src/DeskPilot.Desktop/ViewModels/VoiceControlViewModel.cs`
- `src/DeskPilot.Desktop/MainWindow.xaml`
- `tests/DeskPilot.Application.Tests/CommandDispatcherTests.cs`
- `tests/DeskPilot.Application.Tests/VoicePipelineCoordinatorTests.cs`
- `tests/DeskPilot.Modules.Tests/AudioControlTests.cs`
- `tests/DeskPilot.Desktop.Tests/DesktopHostRegistrationTests.cs`
- `tests/DeskPilot.Desktop.Tests/VoiceControlViewModelTests.cs`
- `docs/requirements.md`, `docs/architecture.md`, `docs/voice-pipeline.md`, `docs/progress.md`, `docs/roadmap.md`, `README.md`, `CHANGELOG.md`

---

### Task 1: Trusted Command Descriptions and Catalog

**Files:**
- Modify: `src/DeskPilot.Core/Commands/CommandContracts.cs`
- Create: `src/DeskPilot.Modules.Abstractions/Commands/ICommandDescriptionProvider.cs`
- Create: `src/DeskPilot.Application/Commands/CommandCatalog.cs`
- Test: `tests/DeskPilot.Application.Tests/CommandCatalogTests.cs`
- Test: `tests/DeskPilot.Application.Tests/CommandDispatcherTests.cs`

**Interfaces:**
- Consumes: existing `CommandId`, `CommandRequest`, `ICommandHandler`.
- Produces: `CommandPhrasePattern`, request-bearing `IntentResolutionResult`, `ICommandDescriptionProvider`, `ICommandCatalog`, and `CommandCatalog.Commands`.

- [ ] **Step 1: Write the failing catalog tests**

```csharp
[Fact]
public void Constructor_RejectsDescriptionWithoutRegisteredHandler()
{
    var description = new AvailableCommand(
        CommandId.From("audio.set-volume"),
        "Set volume",
        [new CommandPhrasePattern("громкость {percentage}")]);

    var action = () => new CommandCatalog([], [Provider(description)]);

    action.Should().Throw<InvalidOperationException>()
        .WithMessage("*audio.set-volume*registered handler*");
}

[Fact]
public void Constructor_RejectsDuplicateNormalizedPattern()
{
    var first = Description("audio.first", "Сделай громче");
    var second = Description("audio.second", "  сделай   громче  ");
    var action = () => new CommandCatalog(
        [Handler("audio.first"), Handler("audio.second")],
        [Provider(first, second)]);

    action.Should().Throw<InvalidOperationException>()
        .WithMessage("*duplicate command phrase*");
}

[Fact]
public async Task DispatchAsync_UnknownCommand_ReturnsStableErrorCode()
{
    var dispatcher = new CommandDispatcher([]);
    var result = await dispatcher.DispatchAsync(
        new CommandRequest(CommandId.From("unknown")),
        CancellationToken.None);

    result.Status.Should().Be(CommandExecutionStatus.NotFound);
    result.ErrorCode.Should().Be("command-not-found");
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

```powershell
dotnet test .\tests\DeskPilot.Application.Tests\DeskPilot.Application.Tests.csproj --filter "FullyQualifiedName~CommandCatalogTests|FullyQualifiedName~CommandDispatcherTests"
```

Expected: compilation fails because the new phrase, catalog, provider, and error-code contracts do not exist.

- [ ] **Step 3: Add the Core contracts**

```csharp
public sealed record CommandPhrasePattern(
    string Pattern,
    IReadOnlyDictionary<string, string>? Arguments = null)
{
    public const string PercentagePlaceholder = "{percentage}";
}

public sealed record AvailableCommand(
    CommandId CommandId,
    string DisplayName,
    IReadOnlyCollection<CommandPhrasePattern> Phrases);

public sealed record IntentResolutionResult(
    IntentResolutionStatus Status,
    CommandRequest? Request,
    double Confidence)
{
    public static IntentResolutionResult NotFound { get; } =
        new(IntentResolutionStatus.NotFound, null, 0);
}

public sealed record CommandExecutionResult(
    CommandId CommandId,
    CommandExecutionStatus Status,
    string? Message = null)
{
    public string? ErrorCode { get; init; }

    public static CommandExecutionResult Succeeded(CommandId commandId) =>
        new(commandId, CommandExecutionStatus.Succeeded);

    public static CommandExecutionResult NotFound(CommandId commandId) =>
        new(commandId, CommandExecutionStatus.NotFound, "The command is not registered.")
        {
            ErrorCode = "command-not-found",
        };
}
```

- [ ] **Step 4: Add the provider and validated catalog**

```csharp
public interface ICommandDescriptionProvider
{
    IReadOnlyCollection<AvailableCommand> GetCommands();
}

public interface ICommandCatalog
{
    IReadOnlyCollection<AvailableCommand> Commands { get; }
}

public sealed class CommandCatalog : ICommandCatalog
{
    public CommandCatalog(
        IEnumerable<ICommandHandler> handlers,
        IEnumerable<ICommandDescriptionProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(providers);
        var registered = handlers.Select(static item => item.CommandId).ToHashSet();
        var commands = providers.SelectMany(static item => item.GetCommands()).ToArray();
        foreach (var command in commands)
        {
            if (!registered.Contains(command.CommandId))
            {
                throw new InvalidOperationException(
                    $"Command '{command.CommandId}' has no registered handler.");
            }

            if (string.IsNullOrWhiteSpace(command.DisplayName) || command.Phrases.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Command '{command.CommandId}' has an invalid description.");
            }
        }

        var duplicateId = commands.GroupBy(static item => item.CommandId)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateId is not null)
        {
            throw new InvalidOperationException(
                $"Command '{duplicateId.Key}' has duplicate descriptions.");
        }

        var duplicatePattern = commands
            .SelectMany(static command => command.Phrases.Select(phrase =>
                NormalizeForValidation(phrase.Pattern)))
            .GroupBy(static pattern => pattern, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicatePattern is not null)
        {
            throw new InvalidOperationException(
                $"The duplicate command phrase '{duplicatePattern.Key}' is not allowed.");
        }

        Commands = commands;
    }

    public IReadOnlyCollection<AvailableCommand> Commands { get; }

    private static string NormalizeForValidation(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = string.Join(' ', value.Trim().ToLowerInvariant()
            .Replace('ё', 'е')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var placeholderCount = normalized.Split(
            CommandPhrasePattern.PercentagePlaceholder,
            StringSplitOptions.None).Length - 1;
        var withoutApprovedPlaceholder = normalized.Replace(
            CommandPhrasePattern.PercentagePlaceholder,
            string.Empty,
            StringComparison.Ordinal);
        if (placeholderCount > 1
            || withoutApprovedPlaceholder.Contains('{')
            || withoutApprovedPlaceholder.Contains('}'))
        {
            throw new InvalidOperationException(
                $"Command phrase '{value}' has invalid placeholders.");
        }

        return normalized;
    }
}
```

- [ ] **Step 5: Run focused tests and verify GREEN**

```powershell
dotnet test .\tests\DeskPilot.Application.Tests\DeskPilot.Application.Tests.csproj --filter "FullyQualifiedName~CommandCatalogTests|FullyQualifiedName~CommandDispatcherTests"
```

Expected: all catalog and dispatcher tests pass.

- [ ] **Step 6: Commit Task 1**

```powershell
git add src/DeskPilot.Core/Commands src/DeskPilot.Modules.Abstractions/Commands src/DeskPilot.Application/Commands tests/DeskPilot.Application.Tests
git commit -m "feat: add trusted voice command catalog"
```

---

### Task 2: Russian Normalization, Numbers, and Exact Resolution

**Files:**
- Create: `src/DeskPilot.Application/Intents/CommandTextNormalizer.cs`
- Create: `src/DeskPilot.Application/Intents/RussianVolumeNumberParser.cs`
- Create: `src/DeskPilot.Application/Intents/PhrasePatternMatcher.cs`
- Create: `src/DeskPilot.Application/Intents/ExactPhraseIntentResolver.cs`
- Modify: `src/DeskPilot.Application/Intents/IIntentResolver.cs`
- Test: `tests/DeskPilot.Application.Tests/IntentResolverTests.cs`

**Interfaces:**
- Consumes: `AvailableCommand`, `CommandPhrasePattern`, `CommandRequest`, `IntentResolutionResult` from Task 1.
- Produces: `ICommandTextNormalizer.Normalize`, `RussianVolumeNumberParser.TryParse`, `PhrasePatternMatcher.MatchExact`, and `ExactPhraseIntentResolver.ResolveAsync`.

- [ ] **Step 1: Write failing normalizer and number tests**

```csharp
[Theory]
[InlineData("  ВКЛЮЧИ,   ЗВУК! ", "включи звук")]
[InlineData("Убери ёлочную пунктуацию...", "убери елочную пунктуацию")]
public void Normalize_ReturnsCanonicalRussianText(string input, string expected)
{
    new CommandTextNormalizer().Normalize(input).Should().Be(expected);
}

[Theory]
[InlineData("0", 0)]
[InlineData("100", 100)]
[InlineData("ноль", 0)]
[InlineData("девятнадцать", 19)]
[InlineData("сорок пять", 45)]
[InlineData("девяносто девять", 99)]
[InlineData("сто", 100)]
public void TryParse_AcceptsApprovedRange(string input, int expected)
{
    var result = new RussianVolumeNumberParser().TryParse(input, out var value);
    result.Should().BeTrue();
    value.Should().Be(expected);
}

[Theory]
[InlineData("-1")]
[InlineData("101")]
[InlineData("сорок пятьдесят")]
[InlineData("сорок пять и шесть")]
[InlineData("10.5")]
public void TryParse_RejectsUnsafeValue(string input)
{
    new RussianVolumeNumberParser().TryParse(input, out _).Should().BeFalse();
}
```

- [ ] **Step 2: Write failing exact resolver tests**

```csharp
[Fact]
public async Task ExactResolver_ResolvesLiteralPhraseWithStaticArguments()
{
    var resolver = CreateExactResolver();
    var command = Command(
        "audio.change-volume",
        new CommandPhrasePattern("сделай громче", Args(("delta", "10"))));

    var result = await resolver.ResolveAsync(
        "Сделай, громче!",
        [command],
        CancellationToken.None);

    result.Status.Should().Be(IntentResolutionStatus.Resolved);
    result.Request!.CommandId.Value.Should().Be("audio.change-volume");
    result.Request.Arguments.Should().Contain("delta", "10");
    result.Confidence.Should().Be(1);
}

[Theory]
[InlineData("громкость 40", "40")]
[InlineData("громкость один процент", "1")]
[InlineData("громкость два процента", "2")]
[InlineData("громкость сорок пять процентов", "45")]
[InlineData("установи громкость сорок пять процентов", "45")]
[InlineData("сделай громкость сто процентов", "100")]
public async Task ExactResolver_ExtractsPercentage(string input, string expected)
{
    var resolver = CreateExactResolver();
    var command = Command(
        "audio.set-volume",
        new CommandPhrasePattern("громкость {percentage}"),
        new CommandPhrasePattern("установи громкость {percentage} процентов"),
        new CommandPhrasePattern("сделай громкость {percentage} процентов"));

    var result = await resolver.ResolveAsync(input, [command], CancellationToken.None);

    result.Request!.Arguments.Should().Contain("percentage", expected);
}

[Fact]
public async Task ExactResolver_DifferentCommandsWithSamePhraseAreAmbiguous()
{
    var resolver = CreateExactResolver();
    var commands = new[]
    {
        Command("audio.first", new CommandPhrasePattern("включи звук")),
        Command("audio.second", new CommandPhrasePattern("включи звук")),
    };

    var result = await resolver.ResolveAsync("включи звук", commands, CancellationToken.None);

    result.Status.Should().Be(IntentResolutionStatus.Ambiguous);
    result.Request.Should().BeNull();
}
```

- [ ] **Step 3: Run exact resolver tests and verify RED**

```powershell
dotnet test .\tests\DeskPilot.Application.Tests\DeskPilot.Application.Tests.csproj --filter "FullyQualifiedName~IntentResolverTests"
```

Expected: compilation fails because normalization, number, pattern, and exact resolver classes do not exist.

- [ ] **Step 4: Implement normalization and cardinal parsing**

```csharp
public interface ICommandTextNormalizer
{
    string Normalize(string input);
}

public sealed class CommandTextNormalizer : ICommandTextNormalizer
{
    public string Normalize(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        var source = input.Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant()
            .Replace('ё', 'е');
        var buffer = new StringBuilder(source.Length);
        foreach (var character in source)
        {
            buffer.Append(char.IsLetterOrDigit(character) || char.IsWhiteSpace(character)
                ? character
                : ' ');
        }

        return string.Join(' ', buffer.ToString()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

public sealed class RussianVolumeNumberParser
{
    private static readonly IReadOnlyDictionary<string, int> Single =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["ноль"] = 0, ["один"] = 1, ["два"] = 2, ["три"] = 3,
            ["четыре"] = 4, ["пять"] = 5, ["шесть"] = 6, ["семь"] = 7,
            ["восемь"] = 8, ["девять"] = 9, ["десять"] = 10,
            ["одиннадцать"] = 11, ["двенадцать"] = 12, ["тринадцать"] = 13,
            ["четырнадцать"] = 14, ["пятнадцать"] = 15, ["шестнадцать"] = 16,
            ["семнадцать"] = 17, ["восемнадцать"] = 18, ["девятнадцать"] = 19,
            ["двадцать"] = 20, ["тридцать"] = 30, ["сорок"] = 40,
            ["пятьдесят"] = 50, ["шестьдесят"] = 60, ["семьдесят"] = 70,
            ["восемьдесят"] = 80, ["девяносто"] = 90, ["сто"] = 100,
        };

    public bool TryParse(string input, out int value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(input)) return false;
        if (int.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            return value is >= 0 and <= 100;
        }

        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return Single.TryGetValue(parts[0], out value);
        if (parts.Length != 2 || !Single.TryGetValue(parts[0], out var tens)
            || tens is < 20 or > 90 || tens % 10 != 0
            || !Single.TryGetValue(parts[1], out var units) || units is < 1 or > 9)
        {
            value = default;
            return false;
        }

        value = tens + units;
        return true;
    }
}
```

- [ ] **Step 5: Implement exact phrase matching and resolution**

Use this public surface:

```csharp
public sealed record PhraseMatch(CommandRequest Request, string ComparableText);

public sealed class PhrasePatternMatcher(
    ICommandTextNormalizer normalizer,
    RussianVolumeNumberParser numbers)
{
    public PhraseMatch? MatchExact(
        string normalizedInput,
        AvailableCommand command,
        CommandPhrasePattern phrase);

    internal IReadOnlyCollection<PhraseMatch> CreateFuzzyCandidates(
        string normalizedInput,
        AvailableCommand command,
        CommandPhrasePattern phrase);
}

public sealed class ExactPhraseIntentResolver(
    ICommandTextNormalizer normalizer,
    PhrasePatternMatcher matcher) : IIntentResolver
{
    public Task<IntentResolutionResult> ResolveAsync(
        string input,
        IReadOnlyCollection<AvailableCommand> commands,
        CancellationToken cancellationToken);
}
```

`MatchExact` normalizes literal prefix/suffix separately, canonicalizes one optional trailing `процент`, `процента`, or `процентов` token to the pattern form, requires complete word-boundary equality, parses exactly one middle numeric span, merges static arguments with `percentage`, and rejects duplicate keys. Group candidates by command ID plus sorted ordinal argument pairs. Return confidence `1` for one identity, `Ambiguous` with no request for competing identities, and `NotFound` otherwise.

- [ ] **Step 6: Run focused tests and verify GREEN**

```powershell
dotnet test .\tests\DeskPilot.Application.Tests\DeskPilot.Application.Tests.csproj --filter "FullyQualifiedName~IntentResolverTests"
```

Expected: normalization, number parsing, literal resolution, percentage extraction, and exact ambiguity tests pass.

- [ ] **Step 7: Commit Task 2**

```powershell
git add src/DeskPilot.Application/Intents tests/DeskPilot.Application.Tests/IntentResolverTests.cs
git commit -m "feat: add exact Russian audio intent resolution"
```

---

### Task 3: Bounded Fuzzy Resolution and Safe Execution Service

**Files:**
- Create: `src/DeskPilot.Application/Intents/FuzzyPhraseIntentResolver.cs`
- Create: `src/DeskPilot.Application/Intents/CompositeIntentResolver.cs`
- Create: `src/DeskPilot.Application/Voice/VoiceCommandExecutionService.cs`
- Test: `tests/DeskPilot.Application.Tests/IntentResolverTests.cs`
- Test: `tests/DeskPilot.Application.Tests/VoiceCommandExecutionServiceTests.cs`

**Interfaces:**
- Consumes: exact resolver, `PhrasePatternMatcher.CreateFuzzyCandidates`, `ICommandCatalog`, and `ICommandDispatcher`.
- Produces: `FuzzyPhraseIntentResolver`, `CompositeIntentResolver`, `IVoiceCommandExecutionService.ExecuteAsync`, `VoiceCommandExecutionProgress`, and `VoiceCommandExecutionResult`.

- [ ] **Step 1: Write failing fuzzy boundary tests**

```csharp
[Fact]
public async Task FuzzyResolver_AcceptsSingleCandidateAboveThreshold()
{
    var resolver = CreateFuzzyResolver(0.86, 0.08);
    var command = Command(
        "audio.change-volume",
        new CommandPhrasePattern("сделай громче"));

    var result = await resolver.ResolveAsync(
        "сделай громчее",
        [command],
        CancellationToken.None);

    result.Status.Should().Be(IntentResolutionStatus.Resolved);
    result.Request!.CommandId.Value.Should().Be("audio.change-volume");
    result.Confidence.Should().BeGreaterThanOrEqualTo(0.86);
}

[Fact]
public async Task FuzzyResolver_RejectsCandidateBelowThreshold()
{
    var resolver = CreateFuzzyResolver(0.86, 0.08);
    var result = await resolver.ResolveAsync(
        "запусти браузер",
        [Command("audio.set-mute", new CommandPhrasePattern("выключи звук"))],
        CancellationToken.None);

    result.Status.Should().Be(IntentResolutionStatus.NotFound);
}

[Fact]
public async Task FuzzyResolver_RejectsCompetingCommandsInsideMargin()
{
    var resolver = CreateFuzzyResolver(0.60, 0.08);
    var commands = new[]
    {
        Command("audio.first", new CommandPhrasePattern("включи звук")),
        Command("audio.second", new CommandPhrasePattern("выключи звук")),
    };

    var result = await resolver.ResolveAsync("ключи звук", commands, CancellationToken.None);

    result.Status.Should().Be(IntentResolutionStatus.Ambiguous);
    result.Request.Should().BeNull();
}

[Fact]
public async Task CompositeResolver_DoesNotRunFuzzyAfterExactMatch()
{
    var exact = Substitute.For<IIntentResolver>();
    var fuzzy = Substitute.For<IIntentResolver>();
    var resolved = new IntentResolutionResult(
        IntentResolutionStatus.Resolved,
        new CommandRequest(CommandId.From("audio.set-mute")),
        1);
    exact.ResolveAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyCollection<AvailableCommand>>(),
            Arg.Any<CancellationToken>())
        .Returns(resolved);

    var result = await new CompositeIntentResolver(exact, fuzzy)
        .ResolveAsync("выключи звук", [], CancellationToken.None);

    result.Should().Be(resolved);
    await fuzzy.DidNotReceive().ResolveAsync(
        Arg.Any<string>(),
        Arg.Any<IReadOnlyCollection<AvailableCommand>>(),
        Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Write failing execution-service tests**

```csharp
[Fact]
public async Task ExecuteAsync_ResolvedCommand_ReportsProgressThenDispatchesExactRequest()
{
    var request = new CommandRequest(
        CommandId.From("audio.change-volume"),
        new Dictionary<string, string> { ["delta"] = "10" });
    var resolver = Substitute.For<IIntentResolver>();
    resolver.ResolveAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyCollection<AvailableCommand>>(),
            Arg.Any<CancellationToken>())
        .Returns(new IntentResolutionResult(IntentResolutionStatus.Resolved, request, 0.91));
    var dispatcher = Substitute.For<ICommandDispatcher>();
    dispatcher.DispatchAsync(request, Arg.Any<CancellationToken>())
        .Returns(CommandExecutionResult.Succeeded(request.CommandId));
    var progress = new List<VoiceCommandExecutionProgress>();

    var result = await CreateExecutionService(resolver, dispatcher)
        .ExecuteAsync("сделай громче", progress.Add, CancellationToken.None);

    progress.Should().ContainSingle().Which.CommandId.Should().Be(request.CommandId);
    result.Execution!.Status.Should().Be(CommandExecutionStatus.Succeeded);
    await dispatcher.Received(1).DispatchAsync(request, Arg.Any<CancellationToken>());
}

[Theory]
[InlineData(IntentResolutionStatus.NotFound)]
[InlineData(IntentResolutionStatus.Ambiguous)]
public async Task ExecuteAsync_UnresolvedText_NeverDispatches(IntentResolutionStatus status)
{
    var resolver = Substitute.For<IIntentResolver>();
    resolver.ResolveAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyCollection<AvailableCommand>>(),
            Arg.Any<CancellationToken>())
        .Returns(new IntentResolutionResult(status, null, 0));
    var dispatcher = Substitute.For<ICommandDispatcher>();

    var result = await CreateExecutionService(resolver, dispatcher)
        .ExecuteAsync("неизвестная команда", _ => { }, CancellationToken.None);

    result.Execution.Should().BeNull();
    await dispatcher.DidNotReceive().DispatchAsync(
        Arg.Any<CommandRequest>(),
        Arg.Any<CancellationToken>());
}
```

- [ ] **Step 3: Run focused tests and verify RED**

```powershell
dotnet test .\tests\DeskPilot.Application.Tests\DeskPilot.Application.Tests.csproj --filter "FullyQualifiedName~IntentResolverTests|FullyQualifiedName~VoiceCommandExecutionServiceTests"
```

Expected: compilation fails because fuzzy/composite resolvers and the execution service do not exist.

- [ ] **Step 4: Implement normalized Damerau-Levenshtein similarity**

```csharp
internal static double Similarity(string left, string right)
{
    if (left == right) return 1;
    if (left.Length == 0 || right.Length == 0) return 0;
    var matrix = new int[left.Length + 1, right.Length + 1];
    for (var i = 0; i <= left.Length; i++) matrix[i, 0] = i;
    for (var j = 0; j <= right.Length; j++) matrix[0, j] = j;
    for (var i = 1; i <= left.Length; i++)
    {
        for (var j = 1; j <= right.Length; j++)
        {
            var cost = left[i - 1] == right[j - 1] ? 0 : 1;
            matrix[i, j] = Math.Min(
                Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                matrix[i - 1, j - 1] + cost);
            if (i > 1 && j > 1
                && left[i - 1] == right[j - 2]
                && left[i - 2] == right[j - 1])
            {
                matrix[i, j] = Math.Min(
                    matrix[i, j],
                    matrix[i - 2, j - 2] + cost);
            }
        }
    }

    return 1d - (double)matrix[left.Length, right.Length]
        / Math.Max(left.Length, right.Length);
}
```

`FuzzyPhraseIntentResolver` uses `CreateFuzzyCandidates`, collapses identical request identities to the highest score, sorts descending, and applies threshold `0.86` plus margin `0.08`. Percentage candidates must contain an exactly parsed number before similarity is calculated; fuzzy matching cannot change the numeric argument.

- [ ] **Step 5: Implement exact-before-fuzzy composition**

```csharp
public sealed class CompositeIntentResolver(
    IIntentResolver exact,
    IIntentResolver fuzzy) : IIntentResolver
{
    public async Task<IntentResolutionResult> ResolveAsync(
        string input,
        IReadOnlyCollection<AvailableCommand> commands,
        CancellationToken cancellationToken)
    {
        var exactResult = await exact.ResolveAsync(input, commands, cancellationToken)
            .ConfigureAwait(false);
        return exactResult.Status == IntentResolutionStatus.NotFound
            ? await fuzzy.ResolveAsync(input, commands, cancellationToken).ConfigureAwait(false)
            : exactResult;
    }
}
```

- [ ] **Step 6: Implement the resolve/progress/dispatch boundary**

```csharp
public sealed record VoiceCommandExecutionProgress(
    CommandId CommandId,
    IntentResolutionStatus IntentStatus,
    double Confidence);

public sealed record VoiceCommandExecutionResult(
    IntentResolutionResult Resolution,
    CommandExecutionResult? Execution);

public interface IVoiceCommandExecutionService
{
    Task<VoiceCommandExecutionResult> ExecuteAsync(
        string recognizedText,
        Action<VoiceCommandExecutionProgress> beforeDispatch,
        CancellationToken cancellationToken);
}

public sealed class VoiceCommandExecutionService(
    IIntentResolver resolver,
    ICommandCatalog catalog,
    ICommandDispatcher dispatcher) : IVoiceCommandExecutionService
{
    public async Task<VoiceCommandExecutionResult> ExecuteAsync(
        string recognizedText,
        Action<VoiceCommandExecutionProgress> beforeDispatch,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recognizedText);
        ArgumentNullException.ThrowIfNull(beforeDispatch);
        var resolution = await resolver.ResolveAsync(
            recognizedText,
            catalog.Commands,
            cancellationToken).ConfigureAwait(false);
        if (resolution.Status != IntentResolutionStatus.Resolved
            || resolution.Request is null)
        {
            return new VoiceCommandExecutionResult(resolution, null);
        }

        beforeDispatch(new VoiceCommandExecutionProgress(
            resolution.Request.CommandId,
            resolution.Status,
            resolution.Confidence));
        var execution = await dispatcher.DispatchAsync(
            resolution.Request,
            cancellationToken).ConfigureAwait(false);
        return new VoiceCommandExecutionResult(resolution, execution);
    }
}
```

- [ ] **Step 7: Run focused tests and verify GREEN**

```powershell
dotnet test .\tests\DeskPilot.Application.Tests\DeskPilot.Application.Tests.csproj --filter "FullyQualifiedName~IntentResolverTests|FullyQualifiedName~VoiceCommandExecutionServiceTests"
```

Expected: exact/fuzzy boundaries and no-dispatch safety tests pass.

- [ ] **Step 8: Commit Task 3**

```powershell
git add src/DeskPilot.Application/Intents src/DeskPilot.Application/Voice/VoiceCommandExecutionService.cs tests/DeskPilot.Application.Tests
git commit -m "feat: add bounded voice command execution"
```

---

### Task 4: Audio Voice Catalog and Preferred-Device Commands

**Files:**
- Create: `src/DeskPilot.Modules.AudioControl/AudioCommandDescriptions.cs`
- Modify: `src/DeskPilot.Modules.AudioControl/AudioControlModule.cs`
- Modify: `src/DeskPilot.Modules.AudioControl/AudioCommandHandlers.cs`
- Test: `tests/DeskPilot.Modules.Tests/AudioControlTests.cs`

**Interfaces:**
- Consumes: `ICommandDescriptionProvider`, `AvailableCommand`, and existing audio contracts.
- Produces: `audio.switch-preferred-device`, `audio.toggle-preferred-device`, and approved Russian descriptions.

- [ ] **Step 1: Write failing module and description tests**

```csharp
[Fact]
public void Module_RegistersPreferredDeviceHandlersAndDescriptionProvider()
{
    var services = new ServiceCollection();
    new AudioControlModule().RegisterServices(services);

    services.Should().Contain(item =>
        item.ImplementationType == typeof(SwitchPreferredDeviceCommandHandler));
    services.Should().Contain(item =>
        item.ImplementationType == typeof(TogglePreferredDeviceCommandHandler));
    services.Should().Contain(item =>
        item.ServiceType == typeof(ICommandDescriptionProvider));
}

[Fact]
public void Descriptions_ContainEveryApprovedAudioIntent()
{
    var descriptions = new AudioCommandDescriptionProvider().GetCommands();

    descriptions.Select(static item => item.CommandId.Value).Should().BeEquivalentTo(
    [
        "audio.switch-preferred-device",
        "audio.toggle-preferred-device",
        "audio.change-volume",
        "audio.set-volume",
        "audio.set-mute",
        "audio.toggle-mute",
    ]);
    descriptions.SelectMany(static item => item.Phrases)
        .Should().Contain(phrase => phrase.Pattern == "сделай громче"
            && phrase.Arguments!["delta"] == "10");
}
```

- [ ] **Step 2: Write failing preferred-device handler tests**

```csharp
[Fact]
public async Task SwitchPreferredDevice_UsesSavedAvailableEndpoint()
{
    var output = Substitute.For<IAudioOutputDeviceService>();
    var preferences = Substitute.For<IAudioPreferredDeviceService>();
    var saved = new AudioOutputDevice("headset", "Headset", true, false);
    preferences.GetAsync(AudioDeviceSlot.Headphones, Arg.Any<CancellationToken>())
        .Returns(saved);
    output.GetDevicesAsync(Arg.Any<CancellationToken>()).Returns([saved]);
    output.SetDefaultDeviceAsync(
            Arg.Any<AudioDeviceSwitchRequest>(),
            Arg.Any<CancellationToken>())
        .Returns(new AudioDeviceSwitchResult(true));
    var handler = new SwitchPreferredDeviceCommandHandler(output, preferences);

    var result = await handler.HandleAsync(
        Request("audio.switch-preferred-device", ("slot", "Headphones")),
        CancellationToken.None);

    result.Status.Should().Be(CommandExecutionStatus.Succeeded);
    await output.Received(1).SetDefaultDeviceAsync(
        new AudioDeviceSwitchRequest("headset", AudioDeviceRole.Multimedia),
        Arg.Any<CancellationToken>());
}

[Fact]
public async Task SwitchPreferredDevice_DisconnectedEndpointFailsWithoutFallback()
{
    var output = Substitute.For<IAudioOutputDeviceService>();
    var preferences = Substitute.For<IAudioPreferredDeviceService>();
    preferences.GetAsync(AudioDeviceSlot.Headphones, Arg.Any<CancellationToken>())
        .Returns(new AudioOutputDevice("headset", "Headset", false, false));
    output.GetDevicesAsync(Arg.Any<CancellationToken>()).Returns([]);
    var handler = new SwitchPreferredDeviceCommandHandler(output, preferences);

    var result = await handler.HandleAsync(
        Request("audio.switch-preferred-device", ("slot", "Headphones")),
        CancellationToken.None);

    result.Status.Should().Be(CommandExecutionStatus.Failed);
    result.ErrorCode.Should().Be("audio-endpoint-unavailable");
    await output.DidNotReceive().SetDefaultDeviceAsync(
        Arg.Any<AudioDeviceSwitchRequest>(),
        Arg.Any<CancellationToken>());
}

[Fact]
public async Task TogglePreferredDevice_CurrentEndpointOutsideSavedPairIsRejected()
{
    var fixture = PreferredDeviceFixture.Create(
        "monitor",
        "speakers",
        "headphones");

    var result = await fixture.Handler.HandleAsync(
        Request("audio.toggle-preferred-device"),
        CancellationToken.None);

    result.Status.Should().Be(CommandExecutionStatus.Rejected);
    result.ErrorCode.Should().Be("audio-current-endpoint-not-preferred");
    await fixture.Output.DidNotReceive().SetDefaultDeviceAsync(
        Arg.Any<AudioDeviceSwitchRequest>(),
        Arg.Any<CancellationToken>());
}
```

- [ ] **Step 3: Run module tests and verify RED**

```powershell
dotnet test .\tests\DeskPilot.Modules.Tests\DeskPilot.Modules.Tests.csproj --filter "FullyQualifiedName~AudioControlTests"
```

Expected: compilation fails because descriptions and preferred-device handlers do not exist.

- [ ] **Step 4: Implement the finite audio phrase provider**

Create one `AvailableCommand` per command ID. Include the exact approved design phrases. The volume descriptions must contain:

```csharp
new AvailableCommand(
    CommandId.From("audio.change-volume"),
    "Изменить громкость",
    [
        Phrase("сделай громче", ("delta", "10")),
        Phrase("громче", ("delta", "10")),
        Phrase("увеличь громкость", ("delta", "10")),
        Phrase("сделай тише", ("delta", "-10")),
        Phrase("тише", ("delta", "-10")),
        Phrase("уменьши громкость", ("delta", "-10")),
    ]),
new AvailableCommand(
    CommandId.From("audio.set-volume"),
    "Установить громкость",
    [
        Phrase("громкость {percentage}"),
        Phrase("установи громкость {percentage}"),
        Phrase("сделай громкость {percentage} процентов"),
    ])
```

Add the remaining descriptions explicitly:

```csharp
new AvailableCommand(
    CommandId.From("audio.switch-preferred-device"),
    "Переключить устройство вывода",
    [
        Phrase("переключи на наушники", ("slot", "Headphones")),
        Phrase("включи наушники", ("slot", "Headphones")),
        Phrase("звук на наушники", ("slot", "Headphones")),
        Phrase("переключи на колонки", ("slot", "Speakers")),
        Phrase("включи колонки", ("slot", "Speakers")),
        Phrase("звук на колонки", ("slot", "Speakers")),
    ]),
new AvailableCommand(
    CommandId.From("audio.toggle-preferred-device"),
    "Переключить сохранённое устройство",
    [
        Phrase("переключи устройство"),
        Phrase("переключи звук между устройствами"),
    ]),
new AvailableCommand(
    CommandId.From("audio.set-mute"),
    "Установить режим звука",
    [
        Phrase("выключи звук", ("muted", "true")),
        Phrase("убери звук", ("muted", "true")),
        Phrase("без звука", ("muted", "true")),
        Phrase("включи звук", ("muted", "false")),
        Phrase("верни звук", ("muted", "false")),
    ]),
new AvailableCommand(
    CommandId.From("audio.toggle-mute"),
    "Переключить режим звука",
    [
        Phrase("переключи мьют"),
        Phrase("переключи беззвучный режим"),
    ])
```

Use an ordinal immutable argument dictionary in `Phrase` and register `AudioCommandDescriptionProvider` as `ICommandDescriptionProvider`.

- [ ] **Step 5: Implement preferred-device handlers and stable codes**

`SwitchPreferredDeviceCommandHandler` parses `slot`, loads that preference, verifies the same endpoint in the active device list, and calls `SetDefaultDeviceAsync`. `TogglePreferredDeviceCommandHandler` requires both preferences and changes only from one saved endpoint to the other; if the current default is outside the pair, reject instead of guessing.

Use these result helpers:

```csharp
public static CommandExecutionResult Rejected(
    CommandRequest request,
    string errorCode,
    string message) =>
    new(request.CommandId, CommandExecutionStatus.Rejected, message)
    {
        ErrorCode = errorCode,
    };

public static CommandExecutionResult ToResult(
    CommandRequest request,
    AudioOperationResult result) =>
    result.IsSuccess
        ? CommandExecutionResult.Succeeded(request.CommandId)
        : new CommandExecutionResult(
            request.CommandId,
            CommandExecutionStatus.Failed,
            result.Message)
        {
            ErrorCode = result.ErrorCode ?? "audio-operation-failed",
        };
```

Use `audio-invalid-arguments`, `audio-preference-missing`, `audio-endpoint-unavailable`, `audio-current-endpoint-not-preferred`, and existing platform codes.

- [ ] **Step 6: Run module tests and verify GREEN**

```powershell
dotnet test .\tests\DeskPilot.Modules.Tests\DeskPilot.Modules.Tests.csproj --filter "FullyQualifiedName~AudioControlTests"
```

Expected: all description, handler, argument, and no-fallback tests pass.

- [ ] **Step 7: Commit Task 4**

```powershell
git add src/DeskPilot.Modules.AudioControl tests/DeskPilot.Modules.Tests/AudioControlTests.cs
git commit -m "feat: add voice audio command handlers"
```

---

### Task 5: Voice Pipeline Resolution, Dispatch, and Feedback

**Files:**
- Modify: `src/DeskPilot.Application/Voice/VoicePipelineStateStore.cs`
- Modify: `src/DeskPilot.Application/Voice/VoicePipelineCoordinator.cs`
- Test: `tests/DeskPilot.Application.Tests/VoicePipelineCoordinatorTests.cs`

**Interfaces:**
- Consumes: `IVoiceCommandExecutionService` and its safe progress/result records.
- Produces: resolving/executing transitions and safe snapshot outcome fields.

- [ ] **Step 1: Write failing success-flow test**

```csharp
[Fact]
public async Task RunSingleCycleAsync_ResolvesDispatchesAndSignalsSuccess()
{
    var fixture = PipelineFixture.Create();
    fixture.Speech.Returns(
        new SpeechRecognitionResult("сделай громче", 0.91, true));
    fixture.Commands.ExecuteAsync(
            "сделай громче",
            Arg.Any<Action<VoiceCommandExecutionProgress>>(),
            Arg.Any<CancellationToken>())
        .Returns(call =>
        {
            call.Arg<Action<VoiceCommandExecutionProgress>>()(
                new VoiceCommandExecutionProgress(
                    CommandId.From("audio.change-volume"),
                    IntentResolutionStatus.Resolved,
                    1));
            return new VoiceCommandExecutionResult(
                new IntentResolutionResult(
                    IntentResolutionStatus.Resolved,
                    new CommandRequest(CommandId.From("audio.change-volume")),
                    1),
                CommandExecutionResult.Succeeded(
                    CommandId.From("audio.change-volume")));
        });

    await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

    fixture.StateHistory.Should().ContainInOrder(
        VoiceAssistantState.RecognizingCommand,
        VoiceAssistantState.ResolvingCommand,
        VoiceAssistantState.ExecutingCommand,
        VoiceAssistantState.Cooldown,
        VoiceAssistantState.WaitingForWakeWord);
    fixture.Signals.ReceivedSignals.Should().Contain(VoiceSignal.Success);
    fixture.State.Snapshot.LastResolvedCommandId.Should().Be("audio.change-volume");
    fixture.State.Snapshot.LastExecutionStatus.Should()
        .Be(CommandExecutionStatus.Succeeded);
}
```

- [ ] **Step 2: Write failing unresolved and failed-command tests**

```csharp
[Theory]
[InlineData(IntentResolutionStatus.NotFound, "voice-command-not-found")]
[InlineData(IntentResolutionStatus.Ambiguous, "voice-command-ambiguous")]
public async Task RunSingleCycleAsync_UnresolvedCommandSignalsFailure(
    IntentResolutionStatus status,
    string expectedCode)
{
    var fixture = PipelineFixture.Create();
    fixture.Commands.ExecuteAsync(
            Arg.Any<string>(),
            Arg.Any<Action<VoiceCommandExecutionProgress>>(),
            Arg.Any<CancellationToken>())
        .Returns(new VoiceCommandExecutionResult(
            new IntentResolutionResult(status, null, 0),
            null));

    await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

    fixture.Signals.ReceivedSignals.Should().Contain(VoiceSignal.Failure);
    fixture.State.Snapshot.ErrorCode.Should().Be(expectedCode);
    fixture.StateHistory.Should().NotContain(VoiceAssistantState.ExecutingCommand);
}

[Fact]
public async Task RunSingleCycleAsync_FailedHandlerPublishesSafeCodeAndRecovers()
{
    var fixture = PipelineFixture.CreateFailedCommand("audio-endpoint-unavailable");

    await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

    fixture.State.Snapshot.LastExecutionStatus.Should()
        .Be(CommandExecutionStatus.Failed);
    fixture.State.Snapshot.ErrorCode.Should().Be("audio-endpoint-unavailable");
    fixture.Signals.ReceivedSignals.Should().Contain(VoiceSignal.Failure);
    fixture.StateHistory.Should().EndWith(VoiceAssistantState.WaitingForWakeWord);
}
```

- [ ] **Step 3: Run coordinator tests and verify RED**

```powershell
dotnet test .\tests\DeskPilot.Application.Tests\DeskPilot.Application.Tests.csproj --filter "FullyQualifiedName~VoicePipelineCoordinatorTests"
```

Expected: compilation fails because coordinator DI and snapshot outcome fields are missing.

- [ ] **Step 4: Extend the safe snapshot**

Add init-only properties without expanding its positional constructor:

```csharp
public string? LastResolvedCommandId { get; init; }
public IntentResolutionStatus? LastIntentStatus { get; init; }
public double? LastIntentConfidence { get; init; }
public CommandExecutionStatus? LastExecutionStatus { get; init; }
```

Clear these fields when a new wake-to-command cycle begins. Never log `LastRecognizedText`.

- [ ] **Step 5: Integrate execution after transcription**

Inject `IVoiceCommandExecutionService commands`. Replace the Milestone 2 success block after speech recognition with:

```csharp
Publish(_state.Snapshot with
{
    State = VoiceAssistantState.ResolvingCommand,
    LastRecognizedText = recognition.Text,
    LastRecognitionConfidence = recognition.Confidence,
    LastResolvedCommandId = null,
    LastIntentStatus = null,
    LastIntentConfidence = null,
    LastExecutionStatus = null,
    ErrorCode = null,
    SafeMessage = null,
});

var commandResult = await _commands.ExecuteAsync(
    recognition.Text,
    progress => Publish(_state.Snapshot with
    {
        State = VoiceAssistantState.ExecutingCommand,
        LastResolvedCommandId = progress.CommandId.Value,
        LastIntentStatus = progress.IntentStatus,
        LastIntentConfidence = progress.Confidence,
    }),
    cancellationToken).ConfigureAwait(false);

var outcome = VoiceCommandOutcomeMapper.Map(commandResult);
Publish(_state.Snapshot with
{
    LastResolvedCommandId = commandResult.Resolution.Request?.CommandId.Value,
    LastIntentStatus = commandResult.Resolution.Status,
    LastIntentConfidence = commandResult.Resolution.Confidence,
    LastExecutionStatus = commandResult.Execution?.Status,
    ErrorCode = outcome.ErrorCode,
    SafeMessage = outcome.SafeMessage,
});
await PlaySignalSafelyAsync(outcome.Signal, cancellationToken).ConfigureAwait(false);
```

Implement a private pure `VoiceCommandOutcomeMapper`: success maps to `VoiceSignal.Success` and `Команда выполнена.`; not found to `voice-command-not-found`; ambiguity to `voice-command-ambiguous`; rejected/failed map to their stable code and a fixed Russian message. Never expose handler or exception text directly.

- [ ] **Step 6: Run all application tests and verify GREEN**

```powershell
dotnet test .\tests\DeskPilot.Application.Tests\DeskPilot.Application.Tests.csproj
```

Expected: all application tests pass, including prior capture/model/recovery cases.

- [ ] **Step 7: Commit Task 5**

```powershell
git add src/DeskPilot.Application/Voice tests/DeskPilot.Application.Tests/VoicePipelineCoordinatorTests.cs
git commit -m "feat: dispatch recognized audio commands"
```

---

### Task 6: Dependency Injection and WPF Command Outcome

**Files:**
- Modify: `src/DeskPilot.Application/Voice/VoiceApplicationServiceCollectionExtensions.cs`
- Modify: `src/DeskPilot.Desktop/App.xaml.cs`
- Modify: `src/DeskPilot.Desktop/ViewModels/VoiceControlViewModel.cs`
- Modify: `src/DeskPilot.Desktop/MainWindow.xaml`
- Test: `tests/DeskPilot.Desktop.Tests/DesktopHostRegistrationTests.cs`
- Test: `tests/DeskPilot.Desktop.Tests/VoiceControlViewModelTests.cs`

**Interfaces:**
- Consumes: application catalog/resolver/execution services and extended snapshot.
- Produces: complete DI graph and safe Russian WPF outcome text.

- [ ] **Step 1: Write failing host registration test**

```csharp
[Fact]
public void CreateHost_ResolvesVoiceCommandExecutionGraph()
{
    using var host = App.CreateHost();

    host.Services.GetRequiredService<ICommandCatalog>()
        .Commands.Should().NotBeEmpty();
    host.Services.GetRequiredService<IIntentResolver>()
        .Should().BeOfType<CompositeIntentResolver>();
    host.Services.GetRequiredService<IVoiceCommandExecutionService>()
        .Should().BeOfType<VoiceCommandExecutionService>();
    host.Services.GetRequiredService<IVoicePipelineController>()
        .Should().BeOfType<VoicePipelineCoordinator>();
}
```

- [ ] **Step 2: Write failing ViewModel projection test**

```csharp
[Fact]
public async Task StateChange_ProjectsResolvedCommandAndSafeOutcome()
{
    var fixture = VoiceViewModelFixture.Create(VoiceSettings.Default, []);
    await fixture.ViewModel.InitializeAsync();
    var snapshot = VoicePipelineSnapshot.Disabled with
    {
        State = VoiceAssistantState.ExecutingCommand,
        LastRecognizedText = "сделай громче",
        LastResolvedCommandId = "audio.change-volume",
        LastIntentStatus = IntentResolutionStatus.Resolved,
        LastIntentConfidence = 1,
        LastExecutionStatus = CommandExecutionStatus.Succeeded,
        SafeMessage = "Команда выполнена.",
    };

    fixture.State.SnapshotChanged +=
        Raise.Event<EventHandler<VoicePipelineSnapshot>>(fixture.State, snapshot);

    fixture.ViewModel.CurrentStateText.Should().Be("Выполняю команду");
    fixture.ViewModel.LastCommandOutcome.Should()
        .Be("audio.change-volume — выполнено");
    fixture.ViewModel.StatusMessage.Should().Be("Команда выполнена.");
}
```

- [ ] **Step 3: Run Desktop tests and verify RED**

```powershell
dotnet test .\tests\DeskPilot.Desktop.Tests\DeskPilot.Desktop.Tests.csproj --filter "FullyQualifiedName~DesktopHostRegistrationTests|FullyQualifiedName~VoiceControlViewModelTests"
```

Expected: DI resolution or compilation fails because services and outcome properties are missing.

- [ ] **Step 4: Register the complete graph**

In `AddDeskPilotVoiceApplication` add:

```csharp
services.AddSingleton<ICommandTextNormalizer, CommandTextNormalizer>();
services.AddSingleton<RussianVolumeNumberParser>();
services.AddSingleton<PhrasePatternMatcher>();
services.AddSingleton<ExactPhraseIntentResolver>();
services.AddSingleton(provider => new FuzzyPhraseIntentResolver(
    provider.GetRequiredService<ICommandTextNormalizer>(),
    provider.GetRequiredService<PhrasePatternMatcher>(),
    0.86,
    0.08));
services.AddSingleton<IIntentResolver>(provider => new CompositeIntentResolver(
    provider.GetRequiredService<ExactPhraseIntentResolver>(),
    provider.GetRequiredService<FuzzyPhraseIntentResolver>()));
services.AddSingleton<ICommandCatalog, CommandCatalog>();
services.AddSingleton<IVoiceCommandExecutionService, VoiceCommandExecutionService>();
```

Keep `ICommandDispatcher` in `App.xaml.cs` and module registration through `ModuleCatalog`. Registration order may remain because resolution happens only after all descriptors are added.

- [ ] **Step 5: Add safe ViewModel and XAML projection**

Add observable `LastCommandOutcome = "—"` and map safe metadata:

```csharp
private static string ToCommandOutcome(VoicePipelineSnapshot snapshot) =>
    snapshot.LastResolvedCommandId is null
        ? snapshot.LastIntentStatus switch
        {
            IntentResolutionStatus.NotFound => "Команда не найдена",
            IntentResolutionStatus.Ambiguous => "Команда неоднозначна",
            _ => "—",
        }
        : snapshot.LastExecutionStatus switch
        {
            CommandExecutionStatus.Succeeded =>
                $"{snapshot.LastResolvedCommandId} — выполнено",
            CommandExecutionStatus.Rejected =>
                $"{snapshot.LastResolvedCommandId} — отклонено",
            CommandExecutionStatus.Failed =>
                $"{snapshot.LastResolvedCommandId} — ошибка",
            _ => snapshot.LastResolvedCommandId,
        };
```

Add explicit state labels:

```csharp
VoiceAssistantState.ResolvingCommand => "Определяю команду",
VoiceAssistantState.ExecutingCommand => "Выполняю команду",
```

Add a third row to the existing voice command Grid: label `Результат:` and binding `Voice.LastCommandOutcome`.

- [ ] **Step 6: Run Desktop tests and verify GREEN**

```powershell
dotnet test .\tests\DeskPilot.Desktop.Tests\DeskPilot.Desktop.Tests.csproj --filter "FullyQualifiedName~DesktopHostRegistrationTests|FullyQualifiedName~VoiceControlViewModelTests"
```

Expected: DI graph and WPF projection tests pass.

- [ ] **Step 7: Run all automated tests before committing**

```powershell
dotnet test .\DeskPilot.sln --configuration Release --no-restore
```

Expected: all solution tests pass with zero failures.

- [ ] **Step 8: Commit Task 6**

```powershell
git add src/DeskPilot.Application/Voice/VoiceApplicationServiceCollectionExtensions.cs src/DeskPilot.Desktop tests/DeskPilot.Desktop.Tests
git commit -m "feat: expose voice audio command outcomes"
```

---

### Task 7: Documentation, Safety Gate, and Milestone Completion

**Files:**
- Create: `docs/voice-commands.md`
- Modify: `docs/requirements.md`
- Modify: `docs/architecture.md`
- Modify: `docs/voice-pipeline.md`
- Modify: `docs/progress.md`
- Modify: `docs/roadmap.md`
- Modify: `README.md`
- Modify: `CHANGELOG.md`
- Verify: entire repository and release-asset workflow.

**Interfaces:**
- Consumes: completed behavior from Tasks 1–6.
- Produces: accurate public Milestone 3 documentation and verification evidence.

- [ ] **Step 1: Write the supported-commands document**

Document exact canonical phrases, `+10/-10` step, digit/Russian cardinal support, saved-device requirements, no-fallback behavior, exact/fuzzy thresholds, local-only processing, and the rule that unknown or ambiguous phrases do nothing.

- [ ] **Step 2: Update architecture and requirements**

Replace the Milestone 2 statement that voice stops before dispatch. Describe `module descriptions -> command catalog -> exact/fuzzy resolver -> execution service -> ICommandDispatcher -> AudioControlModule`, safe observable fields, and log exclusions.

- [ ] **Step 3: Run a fresh restore**

```powershell
dotnet restore .\DeskPilot.sln --force-evaluate
```

Expected: exit `0`; no package downgrade, vulnerability, or restore warning treated as error.

- [ ] **Step 4: Run Release build, tests, and format verification**

```powershell
dotnet build .\DeskPilot.sln --configuration Release --no-restore
dotnet test .\DeskPilot.sln --configuration Release --no-build --no-restore
dotnet format .\DeskPilot.sln --verify-no-changes --no-restore
```

Expected: all commands exit `0`; build has zero warnings/errors; all tests pass; formatter reports no changes.

- [ ] **Step 5: Verify release-model configuration and repository safety**

```powershell
.\scripts\voice-model-assets.ps1 -VerifyConfigurationOnly
git ls-files | Select-String -Pattern '(?i)(\.ggml$|\.bin$|\.onnx$|\.wav$|\.mp3$|\.log$|\.db$|\.sqlite$|\.key$|private.*pem$)'
git grep -n -I -E '(sk-[A-Za-z0-9_-]{20,}|BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY|password\s*=|api[_-]?key\s*=)' -- . ':(exclude)docs/superpowers/plans/2026-07-19-voice-controlled-audio.md'
git status --short --branch
```

Expected: configuration verification exits `0`; tracked-runtime scan has no matches; secret scan has no credential value; only intended changes are present.

- [ ] **Step 6: Run the live WPF smoke on Windows**

```powershell
dotnet run --project .\src\DeskPilot.Desktop\DeskPilot.Desktop.csproj --configuration Release --no-build
```

Verify in order:

1. Select the real microphone and enable voice mode.
2. Say `альфа`, then `переключи на наушники`; confirm the saved available endpoint becomes default.
3. Repeat with `переключи на колонки`.
4. Say `сделай громче`; confirm exactly `+10` with clamp at 100.
5. Say `сделай тише`; confirm exactly `-10` with clamp at 0.
6. Say `установи громкость сорок пять процентов`; confirm 45%.
7. Say `выключи звук`, then `включи звук`; confirm mute state changes.
8. Disconnect the saved Bluetooth endpoint and repeat its switch phrase; confirm failure feedback and no fallback.
9. Say unknown and intentionally ambiguous phrases; confirm no command executes.
10. Confirm WPF shows only safe command ID/status data and returns to wake listening after cooldown.

Expected: all checks pass. Stop the application normally and confirm no process remains.

- [ ] **Step 7: Mark Milestone 3 complete only after Steps 3–6 pass**

Update:

- `docs/roadmap.md`: Milestone 3 checked and Milestone 4 current;
- `docs/progress.md`: Milestone 3 completed, blockers none, Milestone 4 next from updated `develop`;
- `README.md`: local speech, Windows audio control, and deterministic voice execution implemented;
- `CHANGELOG.md`: resolver, audio commands, safe errors, tests, and scope exclusions.

- [ ] **Step 8: Commit the verified milestone**

```powershell
git add src tests docs README.md CHANGELOG.md
git diff --cached --stat
git commit -m "feat: complete voice-controlled audio milestone"
```

- [ ] **Step 9: Push and open the feature PR to develop**

Create `docs/superpowers/plans/2026-07-19-voice-controlled-audio-pr.md` with the milestone summary, exact automated results, live-smoke results, security scans, scope exclusions, and reviewer checklist. Then run:

```powershell
git add docs/superpowers/plans/2026-07-19-voice-controlled-audio-pr.md
git commit -m "docs: prepare milestone 3 pull request"
git push -u origin feature/voice-controlled-audio
gh pr create --base develop --head feature/voice-controlled-audio --draft --title "feat: complete voice-controlled audio milestone" --body-file .\docs\superpowers\plans\2026-07-19-voice-controlled-audio-pr.md
```

Verify base `develop`, head `feature/voice-controlled-audio`, draft state, remote commit equality, and clean worktree. Promotion from `develop` to `main` remains a separate reviewed PR.

## Completion Checklist

- [ ] Command descriptions are module-owned and catalog entries require registered handlers.
- [ ] Russian normalization and `0..100` cardinal/digit parsing are deterministic.
- [ ] Exact matching precedes fuzzy matching with threshold `0.86` and ambiguity margin `0.08`.
- [ ] Fuzzy matching cannot guess or repair a percentage.
- [ ] Only a resolver-built `CommandRequest` reaches `ICommandDispatcher`.
- [ ] Saved headphones/speakers switching never falls back to another endpoint.
- [ ] Louder/quieter changes volume by exactly 10 percentage points.
- [ ] Explicit volume, mute, unmute, and toggle commands execute through `AudioControlModule`.
- [ ] Voice states include resolving/executing; success occurs only after handler success.
- [ ] Unknown, ambiguous, rejected, failed, and unavailable outcomes signal failure and recover.
- [ ] Logs omit recognized text, endpoint IDs, raw arguments, paths, credentials, and audio.
- [ ] Automated, format, release-asset, repository-safety, and live WPF checks pass.
- [ ] Documentation marks Milestone 3 complete and routes Milestone 4 from updated `develop`.
- [ ] Feature branch is pushed and the verified PR targets `develop`.
