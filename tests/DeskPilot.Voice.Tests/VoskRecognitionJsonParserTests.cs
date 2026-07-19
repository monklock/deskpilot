using System.Globalization;
using DeskPilot.Voice.Vosk;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class VoskRecognitionJsonParserTests
{
    [Fact]
    public void Parse_FinalResult_UsesMinimumWordConfidence()
    {
        const string json = """
            {
              "text": "альфа",
              "result": [
                { "conf": 0.94, "start": 0.10, "end": 0.40, "word": "аль" },
                { "conf": 0.82, "start": 0.40, "end": 0.80, "word": "фа" }
              ]
            }
            """;

        var result = VoskRecognitionJsonParser.Parse(json, isFinal: true);

        result.Confidence.Should().Be(0.82);
    }

    [Fact]
    public void Parse_FinalResult_ParsesValidatedWordTiming()
    {
        const string json = """
            {
              "text": "альфа",
              "result": [
                { "conf": 0.94, "start": 0.50, "end": 1.10, "word": "альфа" }
              ]
            }
            """;

        var result = VoskRecognitionJsonParser.Parse(json, isFinal: true);

        result.Words.Should().ContainSingle().Which.Should().Be(
            new VoskWordTiming(
                "альфа",
                0.94,
                TimeSpan.FromSeconds(0.50),
                TimeSpan.FromSeconds(1.10)));
    }

    [Theory]
    [InlineData("{\"text\":\"альфа\"}")]
    [InlineData("{\"text\":\"альфа\",\"result\":[]}")]
    [InlineData("{\"text\":\"альфа\",\"result\":[{\"word\":\"альфа\"}]}")]
    public void Parse_FinalResultWithoutCompleteConfidencePayload_ReturnsZero(string json)
    {
        var result = VoskRecognitionJsonParser.Parse(json, isFinal: true);

        result.Should().Be(new VoskRecognition("альфа", 0, true));
    }

    public static TheoryData<string> InvalidTimedWordPayloads => new()
    {
        { "{\"text\":\"альфа\",\"result\":{}}" },
        { "{\"text\":\"альфа\",\"result\":[null]}" },
        { "{\"text\":\"альфа\",\"result\":[{\"conf\":0.94,\"start\":0.5,\"end\":1.1}] }" },
        { "{\"text\":\"альфа\",\"result\":[{\"word\":\"альфа\",\"start\":0.5,\"end\":1.1}] }" },
        { "{\"text\":\"альфа\",\"result\":[{\"word\":\"альфа\",\"conf\":0.94,\"end\":1.1}] }" },
        { "{\"text\":\"альфа\",\"result\":[{\"word\":\"альфа\",\"conf\":0.94,\"start\":0.5}] }" },
        { "{\"text\":\"альфа\",\"result\":[{\"word\":\"альфа\",\"conf\":1e400,\"start\":0.5,\"end\":1.1}] }" },
        { "{\"text\":\"альфа\",\"result\":[{\"word\":\"альфа\",\"conf\":0.94,\"start\":1e400,\"end\":1.1}] }" },
        { "{\"text\":\"альфа\",\"result\":[{\"word\":\"альфа\",\"conf\":0.94,\"start\":0.5,\"end\":1e400}] }" },
        { "{\"text\":\"альфа\",\"result\":[{\"word\":\"альфа\",\"conf\":0.94,\"start\":-0.1,\"end\":1.1}] }" },
        { "{\"text\":\"альфа\",\"result\":[{\"word\":\"альфа\",\"conf\":0.94,\"start\":1.1,\"end\":0.5}] }" },
        { "{\"text\":\"альфа\",\"result\":[{\"word\":\"альфа\",\"conf\":0.94,\"start\":0.5,\"end\":0.5}] }" },
        { "{\"text\":\"альфа\",\"result\":[{\"word\":\"аль\",\"conf\":0.94,\"start\":0.5,\"end\":0.9},{\"word\":\"фа\",\"conf\":0.92,\"start\":0.8,\"end\":1.1}] }" },
        { "{\"text\":\"альфа\",\"result\":[{\"word\":\"альфа\",\"conf\":-0.01,\"start\":0.5,\"end\":1.1}] }" },
        { "{\"text\":\"альфа\",\"result\":[{\"word\":\"альфа\",\"conf\":1.01,\"start\":0.5,\"end\":1.1}] }" },
    };

    [Theory]
    [MemberData(nameof(InvalidTimedWordPayloads))]
    public void Parse_FinalResultWithInvalidTimedWordPayload_RejectsArrayAtomically(string json)
    {
        var result = VoskRecognitionJsonParser.Parse(json, isFinal: true);

        result.Text.Should().Be("альфа");
        result.Confidence.Should().Be(0);
        result.Words.Should().BeEmpty();
    }

    [Fact]
    public void Parse_PartialResult_ReturnsTextWithoutActivationConfidence()
    {
        const string json = "{\"partial\":\"альфа\"}";

        var result = VoskRecognitionJsonParser.Parse(json, isFinal: false);

        result.Should().Be(new VoskRecognition("альфа", 0, false));
        result.Words.Should().BeEmpty();
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Parse_FinalResultWithOutOfRangeConfidence_ReturnsZero(double confidence)
    {
        var json = $$"""
            { "text": "альфа", "result": [{ "conf": {{confidence.ToString(CultureInfo.InvariantCulture)}}, "start": 0.5, "end": 1.1, "word": "альфа" }] }
            """;

        var result = VoskRecognitionJsonParser.Parse(json, isFinal: true);

        result.Confidence.Should().Be(0);
        result.Words.Should().BeEmpty();
    }

    [Fact]
    public void Parse_InvalidJson_ThrowsInvalidDataException()
    {
        var action = () => VoskRecognitionJsonParser.Parse("not-json", isFinal: true);

        action.Should().Throw<InvalidDataException>()
            .WithMessage("Vosk returned invalid recognition JSON.*");
    }
}
