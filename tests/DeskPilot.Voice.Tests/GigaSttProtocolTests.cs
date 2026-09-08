using System.Text.Json;
using DeskPilot.Voice.Abstractions;
using DeskPilot.Voice.GigaStt;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class GigaSttProtocolTests
{
    [Fact]
    public void Recognition_RejectsMissingConfidence()
    {
        using var json = JsonDocument.Parse("{\"text\":\"альфа\"}");

        var action = () => GigaSttProtocolTestAccess.Recognition(json.RootElement, 0.7);

        action.Should().Throw<SpeechRecognitionException>()
            .Which.Code.Should().Be(SpeechRecognitionFailureCode.ProviderFailure);
    }

    [Fact]
    public void WakeDetection_IgnoresPartialAndNormalizesPunctuation()
    {
        using var json = JsonDocument.Parse("{\"type\":\"partial\",\"text\":\"Альфа\"}");

        GigaSttProtocolTestAccess.Detection(
            json.RootElement,
            new WakeWordOptions("альфа", 0.65),
            0,
            16_000).Should().BeNull();
    }
}

internal static class GigaSttProtocolTestAccess
{
    public static SpeechRecognitionResult Recognition(JsonElement root, double threshold) =>
        GigaSttProtocol.Recognition(root, threshold);

    public static WakeWordDetectionResult? Detection(JsonElement root, WakeWordOptions options, long origin, long consumed) =>
        GigaSttProtocol.Detection(root, options, origin, consumed);
}
