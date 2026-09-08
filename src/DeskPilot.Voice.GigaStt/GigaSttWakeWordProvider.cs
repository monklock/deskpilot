using System.Net.WebSockets;
using System.Text.Json;
using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Voice.GigaStt;

/// <summary>Detects final isolated wake phrases while retaining absolute capture timing.</summary>
public sealed class GigaSttWakeWordProvider(GigaSttRuntime runtime, string bundleDir) : IWakeWordProvider
{
    /// <inheritdoc />
    public string ProviderId => "gigastt";

    /// <inheritdoc />
    public async Task<WakeWordDetectionResult> WaitForDetectionAsync(
        IVoiceAudioCursor audio, WakeWordOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (audio.Format != AudioFormat.Pcm16KhzMono || audio.StartSampleOffset < 0)
        {
            throw new AudioCaptureException(AudioInputResultCode.UnsupportedFormat,
                "GigaSTT требуется поток PCM16 mono 16 кГц.");
        }

        if (string.IsNullOrWhiteSpace(options.Phrase) || !double.IsFinite(options.MinimumConfidence)
            || options.MinimumConfidence is < 0.65 or > 0.90)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        var endpoint = await runtime.EnsureStartedAsync(bundleDir, cancellationToken).ConfigureAwait(false);
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(35));
            await socket.ConnectAsync(new UriBuilder(endpoint) { Scheme = "ws", Path = "v1/ws" }.Uri,
                connect.Token).ConfigureAwait(false);
            using var ready = await ReceiveAsync(socket, connect.Token).ConfigureAwait(false);
            if (GigaSttProtocol.Text(ready.RootElement, "type") != "ready"
                || GigaSttProtocol.Text(ready.RootElement, "version") != "1.0"
                || !ready.RootElement.TryGetProperty("supported_rates", out var rates)
                || rates.ValueKind != JsonValueKind.Array
                || !rates.EnumerateArray().Any(rate => rate.TryGetInt32(out var value) && value == 16_000))
            {
                throw new InvalidDataException("GigaSTT returned an unsupported streaming protocol.");
            }

            await socket.SendAsync(
                "{\"type\":\"configure\",\"protocol_version\":\"1.0\",\"sample_rate\":16000,\"punctuation\":false,\"itn\":false,\"diarization\":false}"u8.ToArray().AsMemory(),
                WebSocketMessageType.Text, true, connect.Token).ConfigureAwait(false);
        }

        long consumed = audio.StartSampleOffset;
        var sender = SendAudioAsync();
        var receiver = ReceiveDetectionAsync();
        try
        {
            var first = await Task.WhenAny(sender, receiver).ConfigureAwait(false);
            if (first == sender)
            {
                await sender.ConfigureAwait(false);
                return await receiver.WaitAsync(TimeSpan.FromSeconds(35), cancellationToken).ConfigureAwait(false);
            }

            return await receiver.ConfigureAwait(false);
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            socket.Abort();
            try { await Task.WhenAll(sender, receiver).ConfigureAwait(false); }
            catch (Exception) when (session.IsCancellationRequested) { }
        }

        async Task SendAudioAsync()
        {
            await foreach (var frame in audio.ReadFramesAsync(session.Token).ConfigureAwait(false))
            {
                if (frame.Pcm16.IsEmpty || frame.Pcm16.Length % 2 != 0
                    || frame.StartSampleOffset != Interlocked.Read(ref consumed)
                    || frame.EndSampleOffset - frame.StartSampleOffset != frame.Pcm16.Length / 2
                    || frame.Duration != TimeSpan.FromTicks(frame.Pcm16.Length / 2L * 625))
                {
                    throw new AudioCaptureException(AudioInputResultCode.UnsupportedFormat,
                        "GigaSTT требуется непрерывный поток PCM16 mono 16 кГц.");
                }

                // Publish the end before sending so a fast response cannot outrun its timing bound.
                Interlocked.Exchange(ref consumed, frame.EndSampleOffset);
                for (var offset = 0; offset < frame.Pcm16.Length; offset += 32_000)
                {
                    using var send = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                    send.CancelAfter(TimeSpan.FromSeconds(35));
                    await socket.SendAsync(frame.Pcm16.Slice(offset, Math.Min(32_000, frame.Pcm16.Length - offset)),
                        WebSocketMessageType.Binary, true, send.Token).ConfigureAwait(false);
                }
            }

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            stop.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.SendAsync("{\"type\":\"stop\"}"u8.ToArray().AsMemory(),
                WebSocketMessageType.Text, true, stop.Token).ConfigureAwait(false);
        }

        async Task<WakeWordDetectionResult> ReceiveDetectionAsync()
        {
            while (true)
            {
                using var json = await ReceiveAsync(socket, session.Token).ConfigureAwait(false);
                var root = json.RootElement;
                if (GigaSttProtocol.Text(root, "type") == "error")
                {
                    throw new InvalidDataException("GigaSTT streaming session failed or reached its limit.");
                }

                var detection = GigaSttProtocol.Detection(root, options, audio.StartSampleOffset,
                    Interlocked.Read(ref consumed));
                if (detection is not null) return detection;
                if (GigaSttProtocol.Text(root, "endpoint_reason") == "stop")
                {
                    throw new EndOfStreamException("Поток завершён до обнаружения ключевой фразы.");
                }
            }
        }
    }

    internal static async Task<JsonDocument> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[8192];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("GigaSTT streaming connection closed or returned a non-text response.");
            if (message.Length + result.Count > GigaSttProtocol.MaximumResponseBytes)
                throw new InvalidDataException("GigaSTT response exceeds the size limit.");
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return JsonDocument.Parse(message.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }
}
