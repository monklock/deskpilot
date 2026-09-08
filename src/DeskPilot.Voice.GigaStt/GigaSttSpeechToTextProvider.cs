using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Text.Json;
using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Voice.GigaStt;

/// <summary>Recognizes bounded PCM command audio through the owned offline process.</summary>
public sealed class GigaSttSpeechToTextProvider(GigaSttRuntime runtime, string bundleDir) : ISpeechToTextProvider
{
    /// <inheritdoc />
    public string ProviderId => "gigastt";

    /// <inheritdoc />
    public async Task<SpeechRecognitionResult> RecognizeAsync(
        CapturedCommandAudio audio, SpeechRecognitionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(options.MinimumConfidence) || options.MinimumConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        var wave = Wave(audio);
        try
        {
            var endpoint = await runtime.EnsureStartedAsync(bundleDir, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var client = GigaSttRuntime.CreateHttpClient();
            using var content = new ByteArrayContent(wave);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(endpoint, "v1/transcribe?punctuation=false&itn=false&diarization=false")) { Content = content };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var json = await GigaSttProtocol.ReadJsonAsync(stream, timeout.Token).ConfigureAwait(false);
            return GigaSttProtocol.Recognition(json.RootElement, options.MinimumConfidence);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            or InvalidDataException or InvalidOperationException or OperationCanceledException)
        {
            throw new SpeechRecognitionException(SpeechRecognitionFailureCode.ProviderFailure,
                "Локальный GigaSTT не смог распознать команду.", exception);
        }
    }

    internal static byte[] Wave(CapturedCommandAudio audio)
    {
        if (audio.Format != AudioFormat.Pcm16KhzMono || audio.Pcm16.Length == 0
            || audio.Pcm16.Length % 2 != 0 || audio.Pcm16.Length > 32_000 * 15)
        {
            throw new SpeechRecognitionException(SpeechRecognitionFailureCode.UnsupportedFormat,
                "GigaSTT требуется непустая запись PCM16 mono 16 кГц длительностью до 15 секунд.");
        }

        var wave = new byte[44 + audio.Pcm16.Length];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4), wave.Length - 8);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), 16_000);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), 32_000);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40), audio.Pcm16.Length);
        audio.Pcm16.Span.CopyTo(wave.AsSpan(44));
        return wave;
    }
}
