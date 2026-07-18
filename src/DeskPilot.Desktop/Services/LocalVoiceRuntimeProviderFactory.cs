using System.IO;
using DeskPilot.Application.Voice;
using DeskPilot.Infrastructure.Data;
using DeskPilot.Voice.Abstractions;
using DeskPilot.Voice.Vosk;
using DeskPilot.Voice.WhisperCpp;

namespace DeskPilot.Desktop.Services;

/// <summary>Resolves active immutable model versions into local provider adapters.</summary>
internal sealed class LocalVoiceRuntimeProviderFactory(
    IAppDataPaths paths,
    VoskWakeWordProviderFactory wakeFactory,
    WhisperSpeechToTextProviderFactory commandFactory) : IVoiceRuntimeProviderFactory
{
    private readonly IAppDataPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly VoskWakeWordProviderFactory _wakeFactory =
        wakeFactory ?? throw new ArgumentNullException(nameof(wakeFactory));
    private readonly WhisperSpeechToTextProviderFactory _commandFactory =
        commandFactory ?? throw new ArgumentNullException(nameof(commandFactory));

    /// <inheritdoc />
    public VoiceRuntimeProviders Create(InstalledVoiceModel wakeModel, InstalledVoiceModel commandModel)
    {
        ArgumentNullException.ThrowIfNull(wakeModel);
        ArgumentNullException.ThrowIfNull(commandModel);
        if (wakeModel.ProviderId != VoiceModelProvider.WakeVosk
            || commandModel.ProviderId != VoiceModelProvider.CommandWhisper)
        {
            throw Unavailable();
        }

        var wakeVersionDirectory = ResolveVersionDirectory(wakeModel.RelativePath);
        var wakePath = ResolveVoskModelDirectory(wakeVersionDirectory);
        var commandDirectory = ResolveVersionDirectory(commandModel.RelativePath);
        var commandFiles = Directory.GetFiles(commandDirectory, "*.bin", SearchOption.TopDirectoryOnly);
        if (commandFiles.Length != 1)
        {
            throw Unavailable();
        }

        return new VoiceRuntimeProviders(
            _wakeFactory(wakePath),
            _commandFactory(Path.GetFullPath(commandFiles[0])));
    }

    private string ResolveVersionDirectory(string relativePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            {
                throw Unavailable();
            }

            var root = Path.GetFullPath(_paths.ModelsRootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(candidate))
            {
                throw Unavailable();
            }

            return candidate;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            throw Unavailable();
        }
    }

    private static string ResolveVoskModelDirectory(string versionDirectory)
    {
        if (IsVoskModelDirectory(versionDirectory))
        {
            return versionDirectory;
        }

        var candidates = Directory
            .GetDirectories(versionDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(IsVoskModelDirectory)
            .ToArray();
        return candidates.Length == 1 ? Path.GetFullPath(candidates[0]) : throw Unavailable();
    }

    private static bool IsVoskModelDirectory(string path) =>
        File.Exists(Path.Combine(path, "am", "final.mdl"))
        && File.Exists(Path.Combine(path, "conf", "mfcc.conf"))
        && File.Exists(Path.Combine(path, "conf", "model.conf"))
        && File.Exists(Path.Combine(path, "graph", "Gr.fst"))
        && File.Exists(Path.Combine(path, "graph", "HCLr.fst"))
        && File.Exists(Path.Combine(path, "graph", "phones", "word_boundary.int"));

    private static InvalidOperationException Unavailable() =>
        new("Active voice model is unavailable.");
}
