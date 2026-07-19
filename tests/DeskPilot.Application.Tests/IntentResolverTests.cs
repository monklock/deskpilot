using DeskPilot.Application.Intents;
using DeskPilot.Core.Commands;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Application.Tests;

public sealed class IntentResolverTests
{
    [Theory]
    [InlineData("  ВКЛЮЧИ,   ЗВУК! ", "включи звук")]
    [InlineData("Убери ёлочную пунктуацию...", "убери елочную пунктуацию")]
    [InlineData("ГРОМКОСТЬ—СОРОК", "громкость сорок")]
    public void Normalize_ReturnsCanonicalRussianText(string input, string expected)
    {
        new CommandTextNormalizer().Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Normalize_RejectsBlankInput()
    {
        var action = () => new CommandTextNormalizer().Normalize("   ");

        action.Should().Throw<ArgumentException>();
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
    [InlineData("")]
    public void TryParse_RejectsUnsafeValue(string input)
    {
        new RussianVolumeNumberParser().TryParse(input, out _).Should().BeFalse();
    }

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

        result.Status.Should().Be(IntentResolutionStatus.Resolved);
        result.Request!.Arguments.Should().Contain("percentage", expected);
    }

    [Theory]
    [InlineData("громкость 101")]
    [InlineData("громкость минус один")]
    [InlineData("громкость сорок пять и шесть")]
    [InlineData("громкость")]
    public async Task ExactResolver_RejectsInvalidPercentage(string input)
    {
        var resolver = CreateExactResolver();
        var command = Command(
            "audio.set-volume",
            new CommandPhrasePattern("громкость {percentage}"));

        var result = await resolver.ResolveAsync(input, [command], CancellationToken.None);

        result.Status.Should().Be(IntentResolutionStatus.NotFound);
        result.Request.Should().BeNull();
    }

    [Fact]
    public async Task ExactResolver_CollapsesEquivalentRequestsFromMultiplePatterns()
    {
        var resolver = CreateExactResolver();
        var command = Command(
            "audio.set-volume",
            new CommandPhrasePattern("громкость {percentage}"),
            new CommandPhrasePattern("громкость {percentage} процентов"));

        var result = await resolver.ResolveAsync(
            "громкость 40 процентов",
            [command],
            CancellationToken.None);

        result.Status.Should().Be(IntentResolutionStatus.Resolved);
        result.Request!.Arguments.Should().Contain("percentage", "40");
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

        var result = await resolver.ResolveAsync(
            "включи звук",
            commands,
            CancellationToken.None);

        result.Status.Should().Be(IntentResolutionStatus.Ambiguous);
        result.Request.Should().BeNull();
    }

    [Fact]
    public async Task ExactResolver_PropagatesCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var resolver = CreateExactResolver();

        var action = () => resolver.ResolveAsync(
            "включи звук",
            [Command("audio.set-mute", new CommandPhrasePattern("включи звук"))],
            source.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    private static ExactPhraseIntentResolver CreateExactResolver()
    {
        var normalizer = new CommandTextNormalizer();
        return new ExactPhraseIntentResolver(
            normalizer,
            new PhrasePatternMatcher(normalizer, new RussianVolumeNumberParser()));
    }

    private static AvailableCommand Command(
        string commandId,
        params CommandPhrasePattern[] phrases) =>
        new(CommandId.From(commandId), commandId, phrases);

    private static IReadOnlyDictionary<string, string> Args(
        params (string Key, string Value)[] arguments) =>
        arguments.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
}
