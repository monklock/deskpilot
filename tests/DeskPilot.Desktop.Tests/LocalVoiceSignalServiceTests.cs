using System.Media;
using DeskPilot.Desktop.Services;
using DeskPilot.Voice.Abstractions;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Desktop.Tests;

public sealed class LocalVoiceSignalServiceTests
{
    [Fact]
    public void Resolve_MapsEveryVoiceSignalToFixedSystemSound()
    {
        SystemVoiceTonePlayer.Resolve(VoiceSignal.Ready)
            .Should().BeSameAs(SystemSounds.Asterisk);
        SystemVoiceTonePlayer.Resolve(VoiceSignal.Success)
            .Should().BeSameAs(SystemSounds.Exclamation);
        SystemVoiceTonePlayer.Resolve(VoiceSignal.Failure)
            .Should().BeSameAs(SystemSounds.Hand);
    }

    [Fact]
    public async Task PlayAsync_RunsToneOutsideCallerAndReturnsAwaitableCompletion()
    {
        var player = new BlockingTonePlayer();
        var service = new LocalVoiceSignalService(player);

        var playback = service.PlayAsync(VoiceSignal.Ready, CancellationToken.None);
        await player.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        playback.IsCompleted.Should().BeFalse();
        player.Signal.Should().Be(VoiceSignal.Ready);
        player.Release.TrySetResult();
        await playback;
    }

    [Fact]
    public async Task PlayAsync_AlreadyCancelled_DoesNotPlayTone()
    {
        var player = new BlockingTonePlayer();
        var service = new LocalVoiceSignalService(player);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var action = () => service.PlayAsync(VoiceSignal.Failure, cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        player.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData(VoiceSignal.Success)]
    [InlineData(VoiceSignal.Failure)]
    public async Task PlayAsync_SuccessOrFailure_RunsToneOutsideCaller(VoiceSignal signal)
    {
        var player = new BlockingTonePlayer();
        var service = new LocalVoiceSignalService(player);

        var playback = service.PlayAsync(signal, CancellationToken.None);
        await player.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        playback.IsCompleted.Should().BeFalse();
        player.Signal.Should().Be(signal);
        player.Release.TrySetResult();
        await playback;
    }

    [Fact]
    public void Resolve_UnknownSignal_ThrowsArgumentOutOfRangeException()
    {
        var action = () => SystemVoiceTonePlayer.Resolve((VoiceSignal)999);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    private sealed class BlockingTonePlayer : ISystemVoiceTonePlayer
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public VoiceSignal? Signal { get; private set; }

        public int CallCount { get; private set; }

        public void Play(VoiceSignal signal)
        {
            CallCount++;
            Signal = signal;
            Started.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
        }
    }
}
