using System.IO;
using DeskPilot.Application.Voice;
using DeskPilot.Infrastructure.Data;
using DeskPilot.Voice.Abstractions;
using DeskPilot.Voice.GigaStt;

namespace DeskPilot.Desktop.Services;

/// <summary>Resolves active immutable model versions into local provider adapters.</summary>
internal sealed class LocalVoiceRuntimeProviderFactory(
    IAppDataPaths paths,
    GigaSttRuntime runtime) : IVoiceRuntimeProviderFactory
{
    private readonly IAppDataPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly GigaSttRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    /// <inheritdoc />
    public VoiceRuntimeProviders Create(InstalledVoiceModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.ProviderId != VoiceModelProvider.GigaStt)
        {
            throw Unavailable();
        }

        var bundleDirectory = ResolveVersionDirectory(model.RelativePath);
        return new VoiceRuntimeProviders(
            new GigaSttWakeWordProvider(_runtime, bundleDirectory),
            new GigaSttSpeechToTextProvider(_runtime, bundleDirectory));
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => _runtime.StopAsync(cancellationToken);

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

            for (var directory = new DirectoryInfo(candidate); directory is not null; directory = directory.Parent)
            {
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw Unavailable();
                }
            }

            return candidate;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw Unavailable();
        }
    }

    private static InvalidOperationException Unavailable() =>
        new("Active voice model is unavailable.");
}
