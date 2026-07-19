using System.Runtime.InteropServices;

namespace DeskPilot.Voice.Vosk;

internal static class VoskNativeMethods
{
    private const string LibraryName = "libvosk";

    [DllImport(LibraryName, EntryPoint = "vosk_model_new", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ModelNew(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath);

    [DllImport(LibraryName, EntryPoint = "vosk_model_free", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ModelFree(IntPtr model);

    [DllImport(LibraryName, EntryPoint = "vosk_recognizer_new_grm", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr RecognizerNewGrammar(
        IntPtr model,
        float sampleRate,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string grammar);

    [DllImport(LibraryName, EntryPoint = "vosk_recognizer_set_words", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void RecognizerSetWords(IntPtr recognizer, int enabled);

    [DllImport(LibraryName, EntryPoint = "vosk_recognizer_set_partial_words", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void RecognizerSetPartialWords(IntPtr recognizer, int enabled);

    [DllImport(LibraryName, EntryPoint = "vosk_recognizer_accept_waveform", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int RecognizerAcceptWaveform(
        IntPtr recognizer,
        [In] byte[] buffer,
        int length);

    [DllImport(LibraryName, EntryPoint = "vosk_recognizer_result", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr RecognizerResult(IntPtr recognizer);

    [DllImport(LibraryName, EntryPoint = "vosk_recognizer_partial_result", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr RecognizerPartialResult(IntPtr recognizer);

    [DllImport(LibraryName, EntryPoint = "vosk_recognizer_final_result", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr RecognizerFinalResult(IntPtr recognizer);

    [DllImport(LibraryName, EntryPoint = "vosk_recognizer_free", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void RecognizerFree(IntPtr recognizer);
}

internal sealed class VoskNativeModel : IDisposable
{
    private IntPtr _handle;

    private VoskNativeModel(IntPtr handle) => _handle = handle;

    public IntPtr Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            return _handle;
        }
    }

    public static VoskNativeModel Load(string modelPath)
    {
        var handle = VoskNativeMethods.ModelNew(modelPath);
        return handle == IntPtr.Zero
            ? throw new InvalidOperationException("The active Vosk model could not be loaded.")
            : new VoskNativeModel(handle);
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            VoskNativeMethods.ModelFree(handle);
        }
    }
}

internal sealed class Utf8VoskNativeRecognizer : IVoskNativeRecognizer
{
    private IntPtr _handle;

    public Utf8VoskNativeRecognizer(IntPtr model, string grammarJson)
    {
        _handle = VoskNativeMethods.RecognizerNewGrammar(model, 16_000f, grammarJson);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("The Vosk recognizer could not be created.");
        }

        VoskNativeMethods.RecognizerSetWords(_handle, 1);
        VoskNativeMethods.RecognizerSetPartialWords(_handle, 0);
    }

    public bool AcceptWaveform(byte[] buffer, int length)
    {
        var result = VoskNativeMethods.RecognizerAcceptWaveform(Handle, buffer, length);
        return result switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidOperationException("Vosk rejected an audio frame."),
        };
    }

    public string Result() => ReadJson(VoskNativeMethods.RecognizerResult(Handle));

    public string PartialResult() => ReadJson(VoskNativeMethods.RecognizerPartialResult(Handle));

    public string FinalResult() => ReadJson(VoskNativeMethods.RecognizerFinalResult(Handle));

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            VoskNativeMethods.RecognizerFree(handle);
        }
    }

    private IntPtr Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            return _handle;
        }
    }

    private static string ReadJson(IntPtr value) =>
        Marshal.PtrToStringUTF8(value)
        ?? throw new InvalidDataException("Vosk returned no recognition JSON.");
}
