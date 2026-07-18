using DeskPilot.Voice.AudioCapture;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class CaptureEndpointChangeBroadcasterTests
{
    [Fact]
    public async Task Publish_TwoActiveWatchers_BothReceiveSignal()
    {
        var broadcaster = new CaptureEndpointChangeBroadcaster();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var first = broadcaster.WatchAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        await using var second = broadcaster.WatchAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var firstMove = first.MoveNextAsync().AsTask();
        var secondMove = second.MoveNextAsync().AsTask();

        broadcaster.Publish();

        (await firstMove).Should().BeTrue();
        (await secondMove).Should().BeTrue();
        first.Current.Should().BeTrue();
        second.Current.Should().BeTrue();
    }
}
