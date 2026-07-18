using System.Runtime.CompilerServices;
using DeskPilot.Voice.Abstractions;
using DeskPilot.Voice.AudioCapture;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class AudioCaptureSessionTests
{
    [Fact]
    public async Task ReadFramesAsync_DataCallbackReturns_FrameOwnsCopiedMemory()
    {
        var client = new FakeCaptureClient("bluetooth-mic", AudioFormat.Pcm16KhzMono);
        await using var session = new NAudioCaptureSession(client);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var frames = session.ReadFramesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var callbackBuffer = new byte[] { 1, 2, 3, 4, 99, 100 };

        client.Emit(callbackBuffer, bytesRecorded: 4);
        callbackBuffer.AsSpan().Fill(0);

        (await frames.MoveNextAsync()).Should().BeTrue();
        frames.Current.Pcm16.ToArray().Should().Equal(1, 2, 3, 4);
        frames.Current.Duration.Should().Be(TimeSpan.FromTicks(1_250));
    }

    [Fact]
    public async Task DataCallback_NormalizationIsBlocked_ReturnsWithoutBlockingNativeCallback()
    {
        var client = new FakeCaptureClient("bluetooth-mic", AudioFormat.Pcm16KhzMono);
        var normalizer = new BlockingAudioBufferNormalizer();
        await using var session = new NAudioCaptureSession(client, normalizer);

        var emitTask = Task.Run(() => client.Emit([1, 2, 3, 4], bytesRecorded: 4));

        await emitTask.WaitAsync(TimeSpan.FromSeconds(1));
        await normalizer.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        normalizer.Release();
    }

    [Fact]
    public async Task ReadFramesAsync_DeviceDisconnect_ThrowsTypedDisconnectedFailure()
    {
        var client = new FakeCaptureClient("bluetooth-mic", AudioFormat.Pcm16KhzMono);
        await using var session = new NAudioCaptureSession(client);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var frames = session.ReadFramesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);

        client.Disconnect(new InvalidOperationException("native details"));
        var action = async () => await frames.MoveNextAsync();

        await action.Should().ThrowAsync<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.Disconnected);
    }

    [Fact]
    public async Task DisposeAsync_StopsOnceUnsubscribesAndDisposesNativeClient()
    {
        var client = new FakeCaptureClient("bluetooth-mic", AudioFormat.Pcm16KhzMono);
        var session = new NAudioCaptureSession(client);

        await session.DisposeAsync();
        await session.DisposeAsync();

        client.StartCount.Should().Be(1);
        client.StopCount.Should().Be(1);
        client.DisposeCount.Should().Be(1);
        client.DataHandlerCount.Should().Be(0);
        client.StoppedHandlerCount.Should().Be(0);
    }

    [Fact]
    public async Task ReadFramesAsync_NormalizationFails_StopsAndDisposesNativeClient()
    {
        var client = new FakeCaptureClient("bluetooth-mic", AudioFormat.Pcm16KhzMono);
        var normalizer = new ThrowingAudioBufferNormalizer();
        await using var session = new NAudioCaptureSession(client, normalizer);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var frames = session.ReadFramesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);

        client.Emit([1, 2, 3, 4], bytesRecorded: 4);
        var action = async () => await frames.MoveNextAsync();

        await action.Should().ThrowAsync<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.UnsupportedFormat);
        client.StopCount.Should().Be(1);
        client.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task OpenAsync_ExplicitEndpointUnavailable_DoesNotCreateFallbackClient()
    {
        var devices = new FakeInputDeviceService(new AudioInputResolution(
            AudioInputResultCode.SelectedDeviceUnavailable,
            null));
        var clients = new FakeCaptureClientFactory();
        var factory = new NAudioCaptureFactory(devices, clients);

        var action = () => factory.OpenAsync("bluetooth-mic", CancellationToken.None);

        await action.Should().ThrowAsync<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.SelectedDeviceUnavailable);
        clients.CreateCount.Should().Be(0);
    }

    [Fact]
    public async Task OpenAsync_ExplicitEndpointAvailable_StartsExactClient()
    {
        var device = new AudioInputDevice("bluetooth-mic", "Bluetooth headset microphone", false, true);
        var devices = new FakeInputDeviceService(new AudioInputResolution(AudioInputResultCode.Success, device));
        var client = new FakeCaptureClient(device.EndpointId, AudioFormat.Pcm16KhzMono);
        var clients = new FakeCaptureClientFactory(client);
        var factory = new NAudioCaptureFactory(devices, clients);

        await using var session = await factory.OpenAsync(device.EndpointId, CancellationToken.None);

        session.EndpointId.Should().Be(device.EndpointId);
        client.StartCount.Should().Be(1);
        clients.RequestedEndpointId.Should().Be(device.EndpointId);
    }

    [Fact]
    public async Task OpenAsync_NativeFactoryFails_ThrowsTypedInitializationFailure()
    {
        var device = new AudioInputDevice("bluetooth-mic", "Bluetooth headset microphone", false, true);
        var devices = new FakeInputDeviceService(new AudioInputResolution(AudioInputResultCode.Success, device));
        var clients = new FakeCaptureClientFactory(new InvalidOperationException("native details"));
        var factory = new NAudioCaptureFactory(devices, clients);

        var action = () => factory.OpenAsync(device.EndpointId, CancellationToken.None);

        await action.Should().ThrowAsync<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.InitializationFailed);
    }

    [Fact]
    public async Task OpenAsync_UnsupportedNativeFormat_DisposesCreatedNativeClient()
    {
        var device = new AudioInputDevice("bluetooth-mic", "Bluetooth headset microphone", false, true);
        var devices = new FakeInputDeviceService(new AudioInputResolution(AudioInputResultCode.Success, device));
        var client = new FakeCaptureClient(device.EndpointId, new AudioFormat(8_000, 1, 8, false));
        var clients = new FakeCaptureClientFactory(client);
        var factory = new NAudioCaptureFactory(devices, clients);

        var action = () => factory.OpenAsync(device.EndpointId, CancellationToken.None);

        await action.Should().ThrowAsync<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.UnsupportedFormat);
        client.StartCount.Should().Be(0);
        client.DisposeCount.Should().Be(1);
    }

    private sealed class FakeCaptureClient(string endpointId, AudioFormat nativeFormat) : IWindowsCaptureClient
    {
        private EventHandler<WindowsCaptureDataEventArgs>? _dataAvailable;
        private EventHandler<WindowsCaptureStoppedEventArgs>? _recordingStopped;

        public string EndpointId { get; } = endpointId;

        public AudioFormat NativeFormat { get; } = nativeFormat;

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int DataHandlerCount => _dataAvailable?.GetInvocationList().Length ?? 0;

        public int StoppedHandlerCount => _recordingStopped?.GetInvocationList().Length ?? 0;

        public event EventHandler<WindowsCaptureDataEventArgs>? DataAvailable
        {
            add => _dataAvailable += value;
            remove => _dataAvailable -= value;
        }

        public event EventHandler<WindowsCaptureStoppedEventArgs>? RecordingStopped
        {
            add => _recordingStopped += value;
            remove => _recordingStopped -= value;
        }

        public void Start() => StartCount++;

        public void Stop() => StopCount++;

        public void Dispose() => DisposeCount++;

        public void Emit(byte[] buffer, int bytesRecorded) =>
            _dataAvailable?.Invoke(this, new WindowsCaptureDataEventArgs(buffer, bytesRecorded));

        public void Disconnect(Exception exception) =>
            _recordingStopped?.Invoke(this, new WindowsCaptureStoppedEventArgs(exception));
    }

    private sealed class BlockingAudioBufferNormalizer : IAudioBufferNormalizer
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public NormalizedAudioBuffer Normalize(ReadOnlySpan<byte> input)
        {
            Started.TrySetResult();
            _release.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            return new NormalizedAudioBuffer(
                input.ToArray(),
                AudioFormat.Pcm16KhzMono,
                TimeSpan.FromTicks(input.Length * TimeSpan.TicksPerSecond / 32_000));
        }

        public void Release() => _release.Set();
    }

    private sealed class ThrowingAudioBufferNormalizer : IAudioBufferNormalizer
    {
        public NormalizedAudioBuffer Normalize(ReadOnlySpan<byte> input) =>
            throw new AudioCaptureException(
                AudioInputResultCode.UnsupportedFormat,
                "Unsupported test format.");
    }

    private sealed class FakeInputDeviceService(AudioInputResolution resolution) : IAudioInputDeviceService
    {
        public Task<IReadOnlyList<AudioInputDevice>> GetActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AudioInputDevice>>(
                resolution.Device is null ? [] : [resolution.Device]);

        public Task<AudioInputResolution> ResolveAsync(
            string? explicitEndpointId,
            CancellationToken cancellationToken) => Task.FromResult(resolution);

        public async IAsyncEnumerable<IReadOnlyList<AudioInputDevice>> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeCaptureClientFactory : IWindowsCaptureClientFactory
    {
        private readonly IWindowsCaptureClient? _client;
        private readonly Exception? _exception;

        public FakeCaptureClientFactory()
        {
        }

        public FakeCaptureClientFactory(IWindowsCaptureClient client) => _client = client;

        public FakeCaptureClientFactory(Exception exception) => _exception = exception;

        public int CreateCount { get; private set; }

        public string? RequestedEndpointId { get; private set; }

        public IWindowsCaptureClient Create(string endpointId)
        {
            CreateCount++;
            RequestedEndpointId = endpointId;
            if (_exception is not null)
            {
                throw _exception;
            }

            return _client ?? throw new InvalidOperationException("No client configured.");
        }
    }
}
