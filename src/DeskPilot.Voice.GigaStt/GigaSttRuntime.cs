using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Voice.GigaStt;

/// <summary>Owns a hidden offline GigaSTT child process and verifies its listening socket.</summary>
public sealed class GigaSttRuntime : IDisposable
{
    /// <summary>Gets the pinned compatible binary version.</summary>
    public const string BinaryVersion = "2.21.0";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private Uri? _endpoint;
    private string? _bundle;
    private bool _disposed;

    /// <summary>Starts or reuses the owned process for this verified model bundle.</summary>
    public async Task<Uri> EnsureStartedAsync(string bundleDir, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleDir);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var bundle = Path.GetFullPath(bundleDir);
            if (_process is { HasExited: false } && _bundle == bundle && _endpoint is not null)
            {
                if (!OwnsPort(_endpoint.Port, _process.Id))
                    throw new InvalidOperationException("GigaSTT no longer owns its local listening socket.");
                return _endpoint;
            }

            await StopCoreAsync().ConfigureAwait(false);
            var executable = Path.Combine(bundle, "bin", "gigastt.exe");
            if (!File.Exists(executable) || !Directory.Exists(Path.Combine(bundle, "models")))
                throw new SpeechRecognitionException(SpeechRecognitionFailureCode.ModelUnavailable,
                    "Локальный комплект GigaSTT отсутствует или повреждён.");

            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Server.ExclusiveAddressUse = true;
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            var start = CreateStartInfo(bundle, port);
            reservation.Stop();
            _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start GigaSTT.");
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _bundle = bundle;
            _endpoint = new Uri($"http://127.0.0.1:{port}/");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            using var client = CreateHttpClient();
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (_process.HasExited) throw new InvalidOperationException("GigaSTT exited before becoming ready.");
                if (OwnsPort(port, _process.Id))
                {
                    try
                    {
                        using var probe = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                        probe.CancelAfter(TimeSpan.FromSeconds(2));
                        using var health = await client.GetAsync(new Uri(_endpoint, "health"),
                            HttpCompletionOption.ResponseHeadersRead, probe.Token).ConfigureAwait(false);
                        health.EnsureSuccessStatusCode();
                        await using var stream = await health.Content.ReadAsStreamAsync(probe.Token).ConfigureAwait(false);
                        using var json = await GigaSttProtocol.ReadJsonAsync(stream, probe.Token).ConfigureAwait(false);
                        if (GigaSttProtocol.Text(json.RootElement, "version") != BinaryVersion)
                            throw new InvalidDataException("GigaSTT binary version does not match the pinned bundle.");
                        using var ready = await client.GetAsync(new Uri(_endpoint, "ready"),
                            HttpCompletionOption.ResponseHeadersRead, probe.Token).ConfigureAwait(false);
                        if (ready.IsSuccessStatusCode && !_process.HasExited && OwnsPort(port, _process.Id))
                            return _endpoint;
                    }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
                }

                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Stops only the child process created by this runtime.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var process = _process;
        _process = null;
        _endpoint = null;
        _bundle = null;
        if (process is null) return;
        using (process)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
    }

    internal static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(2),
    }) { Timeout = Timeout.InfiniteTimeSpan };

    internal static ProcessStartInfo CreateStartInfo(string bundle, int port)
    {
        var start = new ProcessStartInfo(Path.Combine(bundle, "bin", "gigastt.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.Combine(bundle, "bin"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIGASTT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["RUST_LOG"] = "off";
        string[] arguments = ["--offline", "serve", "--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--model-dir", Path.Combine(bundle, "models"), "--model-variant", "rnnt", "--pool-size", "1",
            "--vad", "--vad-model-dir", Path.Combine(bundle, "models", "vad"), "--punctuation", "off", "--itn", "off",
            "--max-session-secs", "0", "--inference-timeout-secs", "30"];
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    internal static bool OwnsPort(int port, int processId)
    {
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        if (result != 122 && result != 0) throw new Win32Exception((int)result);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(buffer, ref size, false, 2, 3, 0);
            if (result != 0) throw new Win32Exception((int)result);
            var count = Marshal.ReadInt32(buffer);
            for (var index = 0; index < count; index++)
            {
                var row = IntPtr.Add(buffer, 4 + index * 24);
                var localPort = (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, 8));
                if (localPort == port)
                    return Marshal.ReadInt32(row, 20) == processId
                        && Marshal.ReadInt32(row, 4) == 0x0100007f;
            }

            return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            StopCoreAsync().GetAwaiter().GetResult();
        }
        finally { _gate.Release(); }
    }
}
