namespace DeskPilot.Voice.Vosk;

internal interface IVoskNativeRecognizer : IDisposable
{
    bool AcceptWaveform(byte[] buffer, int length);

    string Result();

    string PartialResult();

    string FinalResult();
}

/// <summary>Creates native Vosk recognizers with an exact limited grammar.</summary>
public sealed class VoskRecognizerClientFactory : IVoskRecognizerClientFactory
{
    /// <inheritdoc />
    public IVoskRecognizerClient Create(string modelPath, string grammarJson) =>
        new VoskRecognizerClient(modelPath, grammarJson);
}

/// <summary>Owns one native Vosk model and recognizer.</summary>
public sealed class VoskRecognizerClient : IVoskRecognizerClient
{
    private readonly IVoskNativeRecognizer _recognizer;
    private readonly IDisposable _model;
    private int _disposed;

    /// <summary>Loads the selected model and configures one 16 kHz limited-grammar recognizer.</summary>
    public VoskRecognizerClient(string modelPath, string grammarJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(grammarJson);

        global::Vosk.Model? model = null;
        global::Vosk.VoskRecognizer? recognizer = null;
        try
        {
            model = new global::Vosk.Model(modelPath);
            recognizer = new global::Vosk.VoskRecognizer(model, 16_000f, grammarJson);
            recognizer.SetWords(true);
            recognizer.SetPartialWords(false);

            _model = model;
            _recognizer = new NativeVoskRecognizer(recognizer);
        }
        catch
        {
            recognizer?.Dispose();
            model?.Dispose();
            throw;
        }
    }

    internal VoskRecognizerClient(IVoskNativeRecognizer recognizer, IDisposable model)
    {
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
        _model = model ?? throw new ArgumentNullException(nameof(model));
    }

    /// <inheritdoc />
    public VoskRecognition Accept(ReadOnlyMemory<byte> pcm16)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var buffer = pcm16.ToArray();
        var isFinal = _recognizer.AcceptWaveform(buffer, buffer.Length);
        var json = isFinal ? _recognizer.Result() : _recognizer.PartialResult();
        return VoskRecognitionJsonParser.Parse(json, isFinal);
    }

    /// <inheritdoc />
    public VoskRecognition Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return VoskRecognitionJsonParser.Parse(_recognizer.FinalResult(), isFinal: true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _recognizer.Dispose();
        }
        finally
        {
            _model.Dispose();
        }
    }

    private sealed class NativeVoskRecognizer(
        global::Vosk.VoskRecognizer recognizer) : IVoskNativeRecognizer
    {
        public bool AcceptWaveform(byte[] buffer, int length) =>
            recognizer.AcceptWaveform(buffer, length);

        public string Result() => recognizer.Result();

        public string PartialResult() => recognizer.PartialResult();

        public string FinalResult() => recognizer.FinalResult();

        public void Dispose() => recognizer.Dispose();
    }
}
