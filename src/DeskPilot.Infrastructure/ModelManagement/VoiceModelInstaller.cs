using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using DeskPilot.Infrastructure.Data;
using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Infrastructure.ModelManagement;

/// <summary>Downloads and installs immutable voice model versions transactionally.</summary>
public sealed class VoiceModelInstaller : IVoiceModelPackageInstaller
{
    private const int MaximumArchiveEntries = 10_000;
    private readonly IVoiceModelHttpClient _httpClient;
    private readonly IAppDataPaths _paths;
    private readonly IVoiceModelStore _store;
    private readonly HashSet<string> _allowedHosts;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a model installer with an explicit release host allow-list.</summary>
    public VoiceModelInstaller(
        IVoiceModelHttpClient httpClient,
        IAppDataPaths paths,
        IVoiceModelStore store,
        IEnumerable<string> allowedHosts,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(allowedHosts);
        _httpClient = httpClient;
        _paths = paths;
        _store = store;
        _allowedHosts = new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Downloads, verifies, stages, and registers one model version.</summary>
    public async Task<VoiceModelOperationResult> InstallAsync(
        VoiceModelDescriptor model,
        IProgress<VoiceModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        string? downloadPath = null;

        try
        {
            ValidateDescriptor(model, validateDownloadUri: true);
            Directory.CreateDirectory(_paths.ModelDownloadsPath);
            EnsureDiskSpace(GetRequiredDownloadSpace(model));

            downloadPath = Path.Combine(_paths.ModelDownloadsPath, $"{Guid.NewGuid():N}.part");
            var actualHash = await DownloadAsync(model, downloadPath, progress, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, model.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new VoiceModelOperationResult(VoiceModelResultCode.HashMismatch, "Хеш загруженной модели не совпадает.");
            }

            return await InstallVerifiedPayloadAsync(model, downloadPath, progress, cancellationToken, replaceExisting: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new VoiceModelOperationResult(VoiceModelResultCode.Cancelled, "Установка модели отменена.");
        }
        catch (VoiceModelInstallException exception)
        {
            return new VoiceModelOperationResult(exception.Code, exception.SafeMessage);
        }
        catch (VoiceModelCatalogException exception)
        {
            return new VoiceModelOperationResult(exception.Code, exception.Message);
        }
        catch (HttpRequestException)
        {
            return new VoiceModelOperationResult(VoiceModelResultCode.IoFailure, "Не удалось загрузить модель.");
        }
        catch (IOException)
        {
            return new VoiceModelOperationResult(VoiceModelResultCode.IoFailure, "Не удалось сохранить модель.");
        }
        catch (UnauthorizedAccessException)
        {
            return new VoiceModelOperationResult(VoiceModelResultCode.IoFailure, "Недостаточно прав для сохранения модели.");
        }
        finally
        {
            DeleteFile(downloadPath);
        }
    }

    /// <summary>Verifies and installs a model payload included with the application release.</summary>
    public async Task<VoiceModelOperationResult> InstallLocalAsync(
        VoiceModelDescriptor model,
        string payloadPath,
        CancellationToken cancellationToken,
        bool replaceExisting = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadPath);

        try
        {
            ValidateDescriptor(model, validateDownloadUri: false);
            if (!File.Exists(payloadPath))
            {
                return new VoiceModelOperationResult(VoiceModelResultCode.InvalidModel, "Встроенный файл модели отсутствует.");
            }

            EnsureDiskSpace(model.InstalledBytes);
            var actualHash = await ComputeFileHashAsync(payloadPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, model.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new VoiceModelOperationResult(VoiceModelResultCode.HashMismatch, "Хеш встроенной модели не совпадает.");
            }

            return await InstallVerifiedPayloadAsync(model, payloadPath, progress: null, cancellationToken, replaceExisting).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new VoiceModelOperationResult(VoiceModelResultCode.Cancelled, "Установка модели отменена.");
        }
        catch (VoiceModelInstallException exception)
        {
            return new VoiceModelOperationResult(exception.Code, exception.SafeMessage);
        }
        catch (IOException)
        {
            return new VoiceModelOperationResult(VoiceModelResultCode.IoFailure, "Не удалось прочитать встроенную модель.");
        }
        catch (UnauthorizedAccessException)
        {
            return new VoiceModelOperationResult(VoiceModelResultCode.IoFailure, "Недостаточно прав для установки встроенной модели.");
        }
        catch (InvalidOperationException)
        {
            return new VoiceModelOperationResult(VoiceModelResultCode.IoFailure, "Метаданные установленной модели конфликтуют с проверенной версией.");
        }
    }

    private Task<string> DownloadAsync(
        VoiceModelDescriptor model,
        string downloadPath,
        IProgress<VoiceModelProgress>? progress,
        CancellationToken cancellationToken) =>
        _httpClient.GetAsync(
            model.DownloadUri!,
            (response, operationToken) => DownloadResponseAsync(
                response,
                model,
                downloadPath,
                progress,
                operationToken),
            cancellationToken);

    private static async Task<string> DownloadResponseAsync(
        HttpResponseMessage response,
        VoiceModelDescriptor model,
        string downloadPath,
        IProgress<VoiceModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > model.DownloadBytes)
        {
            throw new VoiceModelInstallException(VoiceModelResultCode.InvalidManifest, "Размер модели превышает заявленный лимит.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            downloadPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81_920);
        long received = 0;

        try
        {
            while (true)
            {
                var count = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                received += count;
                if (received > model.DownloadBytes)
                {
                    throw new VoiceModelInstallException(VoiceModelResultCode.InvalidManifest, "Размер модели превышает заявленный лимит.");
                }

                hash.AppendData(buffer, 0, count);
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                TryReportProgress(progress, new VoiceModelProgress(model.Id, received, model.DownloadBytes, "download"));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan());
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task StageAsync(
        VoiceModelDescriptor model,
        string downloadPath,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        if (model.ArchiveFormat == VoiceModelArchiveFormat.None)
        {
            if (new FileInfo(downloadPath).Length > model.InstalledBytes)
            {
                throw new VoiceModelInstallException(VoiceModelResultCode.InvalidManifest, "Размер установленной модели превышает заявленный лимит.");
            }

            var destination = ResolveContainedPath(stagingPath, model.EntryPoint);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(downloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, true);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, true);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            using var archive = ZipFile.OpenRead(downloadPath);
            if (archive.Entries.Count > MaximumArchiveEntries)
            {
                throw new VoiceModelInstallException(VoiceModelResultCode.UnsafeArchive, "Архив модели содержит слишком много файлов.");
            }

            long expandedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectLink(entry);
                expandedBytes = checked(expandedBytes + entry.Length);
                if (expandedBytes > model.InstalledBytes)
                {
                    throw new VoiceModelInstallException(VoiceModelResultCode.UnsafeArchive, "Распакованный размер модели превышает лимит.");
                }

                var destination = ResolveContainedPath(stagingPath, entry.FullName);
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = entry.Open();
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, true);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (InvalidDataException exception)
        {
            throw new VoiceModelInstallException(VoiceModelResultCode.UnsafeArchive, "Архив модели повреждён или небезопасен.", exception);
        }
        catch (OverflowException exception)
        {
            throw new VoiceModelInstallException(VoiceModelResultCode.UnsafeArchive, "Размер архива модели некорректен.", exception);
        }
    }

    private async Task<VoiceModelOperationResult> InstallVerifiedPayloadAsync(
        VoiceModelDescriptor model,
        string payloadPath,
        IProgress<VoiceModelProgress>? progress,
        CancellationToken cancellationToken,
        bool replaceExisting)
    {
        var stagingRoot = Path.Combine(_paths.ModelsRootPath, "staging");
        Directory.CreateDirectory(stagingRoot);
        var stagingPath = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingPath);
        string? backupPath = null;

        try
        {
            await StageAsync(model, payloadPath, stagingPath, cancellationToken).ConfigureAwait(false);
            var entryPointPath = ResolveContainedPath(stagingPath, model.EntryPoint);
            if (!await ValidateProviderModelAsync(model.ProviderId, entryPointPath, cancellationToken).ConfigureAwait(false))
            {
                return new VoiceModelOperationResult(VoiceModelResultCode.InvalidModel, "Структура или заголовок модели не прошли проверку.");
            }

            var finalPath = GetFinalPath(model);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            var reuseVerifiedDirectory = false;
            if (Directory.Exists(finalPath) && !replaceExisting)
            {
                reuseVerifiedDirectory = await DirectoryContentsEqualAsync(
                    stagingPath,
                    finalPath,
                    cancellationToken).ConfigureAwait(false);
                if (!reuseVerifiedDirectory)
                {
                    return new VoiceModelOperationResult(VoiceModelResultCode.InvalidModel, "Существующий каталог версии не прошёл полную проверку.");
                }
            }

            if (Directory.Exists(finalPath) && replaceExisting)
            {
                backupPath = $"{finalPath}.rollback-{Guid.NewGuid():N}";
                Directory.Move(finalPath, backupPath);
            }

            if (!reuseVerifiedDirectory)
            {
                Directory.Move(stagingPath, finalPath);
                stagingPath = string.Empty;
            }

            var relativePath = Path.GetRelativePath(_paths.ModelsRootPath, finalPath);
            await _store.RegisterAsync(
                new InstalledVoiceModel(
                    model.ProviderId,
                    model.Id,
                    model.Version,
                    relativePath,
                    model.Sha256,
                    model.IsBuiltIn ? VoiceModelSource.Seed : VoiceModelSource.Download,
                    false,
                    false,
                    _timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
            TryDeleteDirectory(backupPath);
            backupPath = null;
            TryReportProgress(progress, new VoiceModelProgress(model.Id, model.DownloadBytes, model.DownloadBytes, "installed"));
            return new VoiceModelOperationResult(VoiceModelResultCode.Success);
        }
        catch
        {
            var finalPath = GetFinalPath(model);
            if (backupPath is not null && Directory.Exists(backupPath))
            {
                TryDeleteDirectory(finalPath);
                if (!Directory.Exists(finalPath))
                {
                    Directory.Move(backupPath, finalPath);
                    backupPath = null;
                }
            }
            else if (string.IsNullOrEmpty(stagingPath))
            {
                TryDeleteDirectory(finalPath);
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
        }
    }

    private static async Task<bool> ValidateProviderModelAsync(
        VoiceModelProvider provider,
        string entryPointPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (provider)
        {
            case VoiceModelProvider.CommandWhisper:
                {
                    if (!File.Exists(entryPointPath))
                    {
                        return false;
                    }

                    var header = new byte[4];
                    await using var stream = new FileStream(
                        entryPointPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        header.Length,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    return await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false) == header.Length
                        && BinaryPrimitives.ReadUInt32LittleEndian(header) == 0x67676D6C;
                }
            case VoiceModelProvider.WakeVosk:
                return Directory.Exists(entryPointPath)
                    && File.Exists(Path.Combine(entryPointPath, "am", "final.mdl"))
                    && File.Exists(Path.Combine(entryPointPath, "conf", "mfcc.conf"))
                    && File.Exists(Path.Combine(entryPointPath, "conf", "model.conf"))
                    && File.Exists(Path.Combine(entryPointPath, "graph", "Gr.fst"))
                    && File.Exists(Path.Combine(entryPointPath, "graph", "HCLr.fst"))
                    && File.Exists(Path.Combine(entryPointPath, "graph", "phones", "word_boundary.int"));
            default:
                return false;
        }
    }

    private static async Task<bool> DirectoryContentsEqualAsync(
        string expectedRoot,
        string actualRoot,
        CancellationToken cancellationToken)
    {
        var expected = GetDirectorySnapshot(expectedRoot, cancellationToken);
        var actual = GetDirectorySnapshot(actualRoot, cancellationToken);
        if (expected is null
            || actual is null
            || !expected.Keys.SequenceEqual(actual.Keys, StringComparer.Ordinal))
        {
            return false;
        }

        foreach (var relativePath in expected.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedEntry = expected[relativePath];
            var actualEntry = actual[relativePath];
            if (expectedEntry.IsDirectory != actualEntry.IsDirectory
                || expectedEntry.Length != actualEntry.Length)
            {
                return false;
            }

            if (!expectedEntry.IsDirectory)
            {
                var expectedHash = await ComputeFileHashAsync(expectedEntry.FullPath, cancellationToken).ConfigureAwait(false);
                var actualHash = await ComputeFileHashAsync(actualEntry.FullPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static SortedDictionary<string, DirectorySnapshotEntry>? GetDirectorySnapshot(
        string root,
        CancellationToken cancellationToken)
    {
        var entries = new SortedDictionary<string, DirectorySnapshotEntry>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return null;
                }

                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                var relativePath = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                entries.Add(
                    relativePath,
                    new DirectorySnapshotEntry(
                        path,
                        isDirectory,
                        isDirectory ? 0 : new FileInfo(path).Length));
                if (isDirectory)
                {
                    pending.Push(path);
                }
            }
        }

        return entries;
    }

    private static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private void ValidateDescriptor(VoiceModelDescriptor model, bool validateDownloadUri)
    {
        if (validateDownloadUri
            && (model.DownloadUri is null
            || !string.Equals(model.DownloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !_allowedHosts.Contains(model.DownloadUri.Host))
        )
        {
            throw new VoiceModelInstallException(VoiceModelResultCode.InvalidManifest, "Источник модели не разрешён.");
        }

        ValidatePathSegment(model.Id);
        ValidatePathSegment(model.Version);
        if (model.DownloadBytes <= 0
            || model.InstalledBytes <= 0
            || model.Sha256.Length != 64
            || !model.Sha256.All(Uri.IsHexDigit))
        {
            throw new VoiceModelInstallException(VoiceModelResultCode.InvalidManifest, "Описание модели содержит некорректные ограничения.");
        }

        if (!Version.TryParse(model.MinimumProviderVersion, out var minimumVersion)
            || !Version.TryParse(model.MaximumProviderVersion, out var maximumVersion)
            || minimumVersion > maximumVersion)
        {
            throw new VoiceModelInstallException(VoiceModelResultCode.InvalidManifest, "Диапазон версии провайдера некорректен.");
        }

        var currentVersion = GetProviderVersion(model.ProviderId);
        if (currentVersion < minimumVersion || currentVersion > maximumVersion)
        {
            throw new VoiceModelInstallException(VoiceModelResultCode.Incompatible, "Модель несовместима с текущей версией провайдера.");
        }

        _ = ResolveContainedPath(_paths.ModelsRootPath, model.EntryPoint);
    }

    private static long GetRequiredDownloadSpace(VoiceModelDescriptor model)
    {
        if (model.DownloadBytes > long.MaxValue - model.InstalledBytes)
        {
            throw new VoiceModelInstallException(VoiceModelResultCode.InvalidManifest, "Размер модели некорректен.");
        }

        return model.DownloadBytes + model.InstalledBytes;
    }

    private static Version GetProviderVersion(VoiceModelProvider provider) => provider switch
    {
        VoiceModelProvider.WakeVosk => new Version(0, 3, 38),
        VoiceModelProvider.CommandWhisper => new Version(1, 9, 1),
        _ => throw new VoiceModelInstallException(VoiceModelResultCode.Incompatible, "Провайдер модели не поддерживается."),
    };

    private string GetFinalPath(VoiceModelDescriptor model) => Path.Combine(
        _paths.ModelsRootPath,
        model.ProviderId.ToString(),
        model.Id,
        model.Version);

    private void EnsureDiskSpace(long requiredBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(_paths.ModelsRootPath));
        if (root is null || new DriveInfo(root).AvailableFreeSpace < requiredBytes)
        {
            throw new VoiceModelInstallException(VoiceModelResultCode.InsufficientSpace, "Недостаточно места для установки модели.");
        }
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new VoiceModelInstallException(VoiceModelResultCode.UnsafeArchive, "Архив модели содержит небезопасный путь.");
        }

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new VoiceModelInstallException(VoiceModelResultCode.UnsafeArchive, "Архив модели содержит выход за целевой каталог.");
        }

        return fullPath;
    }

    private static void ValidatePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new VoiceModelInstallException(VoiceModelResultCode.InvalidManifest, "Идентификатор модели небезопасен.");
        }
    }

    private static void RejectLink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        if (unixMode == UnixSymbolicLink || windowsAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new VoiceModelInstallException(VoiceModelResultCode.UnsafeArchive, "Архив модели содержит ссылку.");
        }
    }

    private static void DeleteFile(string? path)
    {
        if (path is not null && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteDirectory(string? path)
    {
        if (path is not null && Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            DeleteDirectory(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryReportProgress(
        IProgress<VoiceModelProgress>? progress,
        VoiceModelProgress value)
    {
        try
        {
            progress?.Report(value);
        }
        catch (Exception)
        {
        }
    }

    private sealed class VoiceModelInstallException : Exception
    {
        public VoiceModelInstallException(VoiceModelResultCode code, string safeMessage, Exception? innerException = null)
            : base(safeMessage, innerException)
        {
            Code = code;
            SafeMessage = safeMessage;
        }

        public VoiceModelResultCode Code { get; }

        public string SafeMessage { get; }
    }

    private sealed record DirectorySnapshotEntry(string FullPath, bool IsDirectory, long Length);
}
