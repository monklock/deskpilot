using System.Runtime.CompilerServices;
using Whisper.net;

namespace DeskPilot.Voice.WhisperCpp;

internal interface IWhisperNativeFactory : IDisposable
{
    IWhisperNativeProcessor CreateProcessor(string language);
}

internal interface IWhisperNativeProcessor : IAsyncDisposable
{
    IAsyncEnumerable<WhisperSegment> ProcessAsync(
        float[] samples,
        CancellationToken cancellationToken);
}

/// <summary>Creates local Whisper.net CPU clients.</summary>
public sealed class WhisperNetClientFactory : IWhisperClientFactory
{
    /// <inheritdoc />
    public IWhisperClient Create(string modelPath) => new WhisperNetClient(modelPath);
}

/// <summary>Owns one loaded Whisper.net model and scoped processors.</summary>
public sealed class WhisperNetClient : IWhisperClient
{
    private readonly IWhisperNativeFactory _factory;
    private int _disposed;

    /// <summary>Loads a local ggml model through the CPU runtime.</summary>
    public WhisperNetClient(string modelPath)
        : this(new WhisperNativeFactory(WhisperFactory.FromPath(modelPath)))
    {
    }

    internal WhisperNetClient(IWhisperNativeFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <inheritdoc />
    public async IAsyncEnumerable<WhisperSegment> ProcessAsync(
        float[] samples,
        string language,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        cancellationToken.ThrowIfCancellationRequested();

        await using var processor = _factory.CreateProcessor(language);
        await foreach (var segment in processor.ProcessAsync(
            samples,
            cancellationToken).ConfigureAwait(false))
        {
            yield return segment;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _factory.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private sealed class WhisperNativeFactory(
        WhisperFactory factory) : IWhisperNativeFactory
    {
        public IWhisperNativeProcessor CreateProcessor(string language) =>
            new WhisperNativeProcessor(
                factory.CreateBuilder()
                    .WithLanguage(language)
                    .Build());

        public void Dispose() => factory.Dispose();
    }

    private sealed class WhisperNativeProcessor(
        WhisperProcessor processor) : IWhisperNativeProcessor
    {
        public async IAsyncEnumerable<WhisperSegment> ProcessAsync(
            float[] samples,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var segment in processor.ProcessAsync(
                samples,
                cancellationToken).ConfigureAwait(false))
            {
                yield return new WhisperSegment(segment.Text, segment.Probability);
            }
        }

        public ValueTask DisposeAsync() => processor.DisposeAsync();
    }
}
