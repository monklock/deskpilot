using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DeskPilot.Voice.Abstractions;
using DeskPilot.Voice.AudioCapture;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class AudioInputDeviceServiceTests
{
    [Fact]
    public async Task ResolveAsync_NoExplicitEndpoint_ReturnsCurrentWindowsDefault()
    {
        var source = new FakeEndpointSource(
        [
            new("usb-mic", "USB microphone", false, true),
            new("laptop-mic", "Laptop microphone", true, true),
        ]);
        var service = new NAudioInputDeviceService(source);

        var result = await service.ResolveAsync(explicitEndpointId: null, CancellationToken.None);

        result.Code.Should().Be(AudioInputResultCode.Success);
        result.Device!.EndpointId.Should().Be("laptop-mic");
    }

    [Fact]
    public async Task ResolveAsync_ExplicitBluetoothEndpointMissing_DoesNotFallBackToDefault()
    {
        var source = new FakeEndpointSource(
        [
            new("laptop-mic", "Laptop microphone", true, true),
        ]);
        var service = new NAudioInputDeviceService(source);

        var result = await service.ResolveAsync("bluetooth-mic", CancellationToken.None);

        result.Code.Should().Be(AudioInputResultCode.SelectedDeviceUnavailable);
        result.Device.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ExplicitEndpointPresent_ReturnsExactEndpoint()
    {
        var source = new FakeEndpointSource(
        [
            new("laptop-mic", "Laptop microphone", true, true),
            new("bluetooth-mic", "Bluetooth headset microphone", false, true),
        ]);
        var service = new NAudioInputDeviceService(source);

        var result = await service.ResolveAsync("bluetooth-mic", CancellationToken.None);

        result.Code.Should().Be(AudioInputResultCode.Success);
        result.Device!.EndpointId.Should().Be("bluetooth-mic");
    }

    [Fact]
    public async Task WatchAsync_SameBluetoothEndpointReappears_BecomesResolvableAgain()
    {
        var source = new FakeEndpointSource(
        [
            new("laptop-mic", "Laptop microphone", true, true),
        ]);
        var service = new NAudioInputDeviceService(source);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var changes = service.WatchAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);

        (await changes.MoveNextAsync()).Should().BeTrue();
        (await service.ResolveAsync("bluetooth-mic", timeout.Token)).Code
            .Should().Be(AudioInputResultCode.SelectedDeviceUnavailable);

        source.Publish(
        [
            new("laptop-mic", "Laptop microphone", true, true),
            new("bluetooth-mic", "Bluetooth headset microphone", false, true),
        ]);

        (await changes.MoveNextAsync()).Should().BeTrue();
        changes.Current.Should().Contain(device => device.EndpointId == "bluetooth-mic");
        (await service.ResolveAsync("bluetooth-mic", timeout.Token)).Code
            .Should().Be(AudioInputResultCode.Success);
    }

    [Fact]
    public async Task WatchAsync_EndpointReturnsAfterInitialSnapshot_DoesNotLoseReconnectSignal()
    {
        var source = new BroadcastEndpointSource(
        [
            new("laptop-mic", "Laptop microphone", true, true),
        ]);
        var service = new NAudioInputDeviceService(source);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var changes = service.WatchAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);

        (await changes.MoveNextAsync()).Should().BeTrue();
        source.Publish(
        [
            new("laptop-mic", "Laptop microphone", true, true),
            new("bluetooth-mic", "Bluetooth headset microphone", false, true),
        ]);

        (await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
        changes.Current.Should().Contain(device => device.EndpointId == "bluetooth-mic");
    }

    private sealed class FakeEndpointSource(IReadOnlyList<AudioInputDevice> devices) : IWindowsCaptureEndpointSource
    {
        private readonly Channel<bool> _changes = Channel.CreateUnbounded<bool>();
        private IReadOnlyList<AudioInputDevice> _devices = devices;

        public Task<IReadOnlyList<AudioInputDevice>> GetActiveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_devices);
        }

        public async IAsyncEnumerable<bool> WatchChangesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var signal in _changes.Reader.ReadAllAsync(cancellationToken))
            {
                yield return signal;
            }
        }

        public void Publish(IReadOnlyList<AudioInputDevice> devices)
        {
            _devices = devices;
            _changes.Writer.TryWrite(true).Should().BeTrue();
        }
    }

    private sealed class BroadcastEndpointSource(IReadOnlyList<AudioInputDevice> devices) : IWindowsCaptureEndpointSource
    {
        private readonly CaptureEndpointChangeBroadcaster _changes = new();
        private IReadOnlyList<AudioInputDevice> _devices = devices;

        public Task<IReadOnlyList<AudioInputDevice>> GetActiveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_devices);
        }

        public IAsyncEnumerable<bool> WatchChangesAsync(CancellationToken cancellationToken) =>
            _changes.WatchAsync(cancellationToken);

        public void Publish(IReadOnlyList<AudioInputDevice> devices)
        {
            _devices = devices;
            _changes.Publish();
        }
    }
}
