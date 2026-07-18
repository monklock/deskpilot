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
                { "conf": 0.94, "word": "альфа" },
                { "conf": 0.82, "word": "альфа" }
              ]
            }
            """;

        var result = VoskRecognitionJsonParser.Parse(json, isFinal: true);

        result.Should().Be(new VoskRecognition("альфа", 0.82, true));
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

    [Fact]
    public void Parse_PartialResult_ReturnsTextWithoutActivationConfidence()
    {
        const string json = "{\"partial\":\"альфа\"}";

        var result = VoskRecognitionJsonParser.Parse(json, isFinal: false);

        result.Should().Be(new VoskRecognition("альфа", 0, false));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Parse_FinalResultWithOutOfRangeConfidence_ReturnsZero(double confidence)
    {
        var json = $$"""
            { "text": "альфа", "result": [{ "conf": {{confidence.ToString(CultureInfo.InvariantCulture)}}, "word": "альфа" }] }
            """;

        var result = VoskRecognitionJsonParser.Parse(json, isFinal: true);

        result.Confidence.Should().Be(0);
    }

    [Fact]
    public void Parse_InvalidJson_ThrowsInvalidDataException()
    {
        var action = () => VoskRecognitionJsonParser.Parse("not-json", isFinal: true);

        action.Should().Throw<InvalidDataException>()
            .WithMessage("Vosk returned invalid recognition JSON.*");
    }
}
