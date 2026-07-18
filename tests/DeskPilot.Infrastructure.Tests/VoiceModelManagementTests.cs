using System.Security.Cryptography;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using DeskPilot.Infrastructure.Data;
using DeskPilot.Infrastructure.ModelManagement;
using DeskPilot.Voice.Abstractions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace DeskPilot.Infrastructure.Tests;

public sealed class VoiceModelManagementTests
{
    [Fact]
    public void Verify_SignatureCreatedByAnotherKey_ReturnsFalse()
    {
        using var trustedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var untrustedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = "{\"schemaVersion\":1}"u8.ToArray();
        var signature = untrustedKey.SignData(manifest, HashAlgorithmName.SHA256);
        var verifier = new VoiceModelManifestVerifier(trustedKey.ExportSubjectPublicKeyInfoPem());

        var result = verifier.Verify(manifest, signature);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ActivateAsync_MarksPreviousVersionAsLastKnownGood()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        await store.RegisterAsync(Model("base-seed", isActive: true), CancellationToken.None);
        await store.RegisterAsync(Model("small-v1", isActive: false), CancellationToken.None);

        await store.ActivateAsync(VoiceModelProvider.CommandWhisper, "small-v1", CancellationToken.None);

        var installed = await store.GetInstalledAsync(VoiceModelProvider.CommandWhisper, CancellationToken.None);
        installed.Single(model => model.Version == "base-seed").IsLastKnownGood.Should().BeTrue();
        installed.Single(model => model.Version == "small-v1").IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task ActivateAsync_MultipleChanges_KeepsOnlyImmediateRollbackVersion()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        await store.RegisterAsync(Model("base-seed", isActive: true), CancellationToken.None);
        await store.RegisterAsync(Model("small-v1", isActive: false), CancellationToken.None);
        await store.RegisterAsync(Model("small-v2", isActive: false), CancellationToken.None);

        await store.ActivateAsync(VoiceModelProvider.CommandWhisper, "small-v1", CancellationToken.None);
        await store.ActivateAsync(VoiceModelProvider.CommandWhisper, "small-v2", CancellationToken.None);

        var installed = await store.GetInstalledAsync(VoiceModelProvider.CommandWhisper, CancellationToken.None);
        installed.Where(model => model.IsLastKnownGood).Should().ContainSingle().Which.Version.Should().Be("small-v1");
    }

    [Fact]
    public async Task RegisterAsync_SameVersionWithDifferentHash_IsRejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        await store.RegisterAsync(Model("base-seed", isActive: true), CancellationToken.None);
        var conflicting = Model("base-seed", isActive: false) with { Sha256 = new string('b', 64) };

        var action = () => store.RegisterAsync(conflicting, CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task InstallAsync_InvalidHash_LeavesActiveVersionAndCleansTemporaryFiles()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        await store.RegisterAsync(Model("base-seed", isActive: true), CancellationToken.None);
        using var root = new TemporaryDirectory();
        var paths = new AppDataPaths(root.Path);
        using var client = CreateClient(new StaticHttpHandler([1, 2, 3]));
        var installer = new VoiceModelInstaller(client, paths, store, ["downloads.example.test"]);
        var descriptor = Descriptor(
            VoiceModelArchiveFormat.None,
            new string('0', 64),
            downloadBytes: 3,
            installedBytes: 3,
            entryPoint: "ggml-base.bin");

        var result = await installer.InstallAsync(descriptor, progress: null, CancellationToken.None);

        result.Code.Should().Be(VoiceModelResultCode.HashMismatch);
        (await store.GetActiveAsync(VoiceModelProvider.CommandWhisper, CancellationToken.None))!.Version.Should().Be("base-seed");
        Directory.Exists(paths.ModelDownloadsPath).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(paths.ModelDownloadsPath).Should().BeEmpty();
    }

    [Fact]
    public async Task InstallAsync_ArchiveTraversal_ReturnsUnsafeArchiveAndCleansStaging()
    {
        var archive = CreateZip(("../escaped.txt", "unsafe"));
        var hash = Convert.ToHexString(SHA256.HashData(archive));
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        using var root = new TemporaryDirectory();
        var paths = new AppDataPaths(root.Path);
        using var client = CreateClient(new StaticHttpHandler(archive));
        var installer = new VoiceModelInstaller(client, paths, store, ["downloads.example.test"]);
        var descriptor = Descriptor(VoiceModelArchiveFormat.Zip, hash, archive.Length, 1024, "model");

        var result = await installer.InstallAsync(descriptor, progress: null, CancellationToken.None);

        result.Code.Should().Be(VoiceModelResultCode.UnsafeArchive);
        File.Exists(Path.Combine(root.Path, "escaped.txt")).Should().BeFalse();
        Directory.Exists(Path.Combine(paths.ModelsRootPath, "staging")).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(Path.Combine(paths.ModelsRootPath, "staging")).Should().BeEmpty();
    }

    [Fact]
    public async Task InstallAsync_ExpandedArchiveExceedsManifestLimit_ReturnsUnsafeArchive()
    {
        var archive = CreateZip(("model/am/data", new string('a', 256)), ("model/conf/settings", "config"));
        var hash = Convert.ToHexString(SHA256.HashData(archive));
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        using var root = new TemporaryDirectory();
        var paths = new AppDataPaths(root.Path);
        using var client = CreateClient(new StaticHttpHandler(archive));
        var installer = new VoiceModelInstaller(client, paths, store, ["downloads.example.test"]);
        var descriptor = Descriptor(VoiceModelArchiveFormat.Zip, hash, archive.Length, 32, "model") with
        {
            ProviderId = VoiceModelProvider.WakeVosk,
        };

        var result = await installer.InstallAsync(descriptor, progress: null, CancellationToken.None);

        result.Code.Should().Be(VoiceModelResultCode.UnsafeArchive);
    }

    [Fact]
    public async Task InstallAsync_ArchiveContainsSymbolicLink_ReturnsUnsafeArchive()
    {
        var archive = CreateZipWithSymbolicLink();
        var hash = Convert.ToHexString(SHA256.HashData(archive));
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        using var root = new TemporaryDirectory();
        var paths = new AppDataPaths(root.Path);
        using var client = CreateClient(new StaticHttpHandler(archive));
        var installer = new VoiceModelInstaller(client, paths, store, ["downloads.example.test"]);

        var result = await installer.InstallAsync(
            Descriptor(VoiceModelArchiveFormat.Zip, hash, archive.Length, 1024, "model"),
            progress: null,
            CancellationToken.None);

        result.Code.Should().Be(VoiceModelResultCode.UnsafeArchive);
    }

    [Fact]
    public async Task GetAsync_InvalidCatalogSignature_ThrowsTypedFailure()
    {
        using var trustedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var untrustedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"models\":[]}");
        var signature = untrustedKey.SignData(manifest, HashAlgorithmName.SHA256);
        var manifestUri = new Uri("https://downloads.example.test/models.manifest.json");
        var signatureUri = new Uri("https://downloads.example.test/models.manifest.sig");
        using var client = CreateClient(new MappedHttpHandler(new Dictionary<Uri, byte[]>
        {
            [manifestUri] = manifest,
            [signatureUri] = signature,
        }));
        var catalog = new VoiceModelCatalogClient(
            client,
            new VoiceModelManifestVerifier(trustedKey.ExportSubjectPublicKeyInfoPem()),
            manifestUri,
            signatureUri,
            ["downloads.example.test"]);

        var action = () => catalog.GetAsync(CancellationToken.None);

        await action.Should().ThrowAsync<VoiceModelCatalogException>()
            .Where(exception => exception.Code == VoiceModelResultCode.InvalidSignature);
    }

    [Fact]
    public async Task InitializeAsync_ValidSeedFile_IsCopiedAndActivatedWithoutNetwork()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        using var root = new TemporaryDirectory();
        var paths = new AppDataPaths(root.Path);
        var seedDirectory = Path.Combine(root.Path, "publish-assets");
        Directory.CreateDirectory(seedDirectory);
        var modelBytes = Encoding.ASCII.GetBytes("lmgg-seed-content");
        await File.WriteAllBytesAsync(Path.Combine(seedDirectory, "ggml-base.bin"), modelBytes);
        var hash = Convert.ToHexString(SHA256.HashData(modelBytes));
        var manifest = $$"""
            {
              "schemaVersion": 1,
              "models": [
                {
                  "id": "whisper-base-multi",
                  "provider": "CommandWhisper",
                  "displayName": "Whisper base multilingual",
                  "version": "openai-base",
                  "qualityTier": "Base",
                  "asset": "ggml-base.bin",
                  "sha256": "{{hash}}",
                  "downloadBytes": {{modelBytes.Length}},
                  "installedBytes": {{modelBytes.Length}},
                  "entryPoint": "ggml-base.bin",
                  "licenseId": "MIT",
                  "archiveFormat": "None",
                  "minimumProviderVersion": "0.0.0",
                  "maximumProviderVersion": "99.0.0"
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(seedDirectory, "seed-manifest.json"), manifest, Encoding.UTF8);
        using var client = CreateClient(new StaticHttpHandler([]));
        var installer = new VoiceModelInstaller(client, paths, store, ["downloads.example.test"]);
        var initializer = new SeedVoiceModelInitializer(seedDirectory, installer, store);

        var result = await initializer.InitializeAsync(CancellationToken.None);

        result.Should().OnlyContain(item => item.Code == VoiceModelResultCode.Success);
        var active = await store.GetActiveAsync(VoiceModelProvider.CommandWhisper, CancellationToken.None);
        active.Should().NotBeNull();
        active!.Source.Should().Be(VoiceModelSource.Seed);
        File.Exists(Path.Combine(paths.ModelsRootPath, active.RelativePath, "ggml-base.bin")).Should().BeTrue();
    }

    [Fact]
    public async Task ActivateAsync_EntersIdleGateBeforeChangingActiveVersion()
    {
        var catalog = Substitute.For<IVoiceModelCatalogSource>();
        var installer = Substitute.For<IVoiceModelPackageInstaller>();
        var seeds = Substitute.For<ISeedVoiceModelSource>();
        var store = Substitute.For<IVoiceModelStore>();
        var gate = Substitute.For<IVoiceModelActivationGate>();
        var lease = Substitute.For<IAsyncDisposable>();
        gate.EnterIdleAsync(Arg.Any<CancellationToken>()).Returns(lease);
        var manager = new VoiceModelManager(catalog, installer, seeds, store, gate);

        var result = await manager.ActivateAsync(
            VoiceModelProvider.CommandWhisper,
            "small-v1",
            CancellationToken.None);

        result.Code.Should().Be(VoiceModelResultCode.Success);
        Received.InOrder(() =>
        {
            gate.EnterIdleAsync(Arg.Any<CancellationToken>());
            store.ActivateAsync(VoiceModelProvider.CommandWhisper, "small-v1", Arg.Any<CancellationToken>());
            lease.DisposeAsync();
        });
    }

    [Fact]
    public async Task RestoreBuiltInAsync_EntersIdleGateAndActivatesRestoredVersion()
    {
        var catalog = Substitute.For<IVoiceModelCatalogSource>();
        var installer = Substitute.For<IVoiceModelPackageInstaller>();
        var seeds = Substitute.For<ISeedVoiceModelSource>();
        seeds.RestoreAsync(VoiceModelProvider.CommandWhisper, Arg.Any<CancellationToken>())
            .Returns((new VoiceModelOperationResult(VoiceModelResultCode.Success), "openai-base"));
        var store = Substitute.For<IVoiceModelStore>();
        var gate = Substitute.For<IVoiceModelActivationGate>();
        gate.EnterIdleAsync(Arg.Any<CancellationToken>()).Returns(Substitute.For<IAsyncDisposable>());
        var manager = new VoiceModelManager(catalog, installer, seeds, store, gate);

        var result = await manager.RestoreBuiltInAsync(VoiceModelProvider.CommandWhisper, CancellationToken.None);

        result.Code.Should().Be(VoiceModelResultCode.Success);
        await gate.Received(1).EnterIdleAsync(Arg.Any<CancellationToken>());
        await store.Received(1).ActivateAsync(
            VoiceModelProvider.CommandWhisper,
            "openai-base",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_IncompatibleProviderVersion_DoesNotStartDownload()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        using var root = new TemporaryDirectory();
        var paths = new AppDataPaths(root.Path);
        var handler = new StaticHttpHandler([1, 2, 3]);
        using var client = CreateClient(handler);
        var installer = new VoiceModelInstaller(client, paths, store, ["downloads.example.test"]);
        var descriptor = Descriptor(
            VoiceModelArchiveFormat.None,
            new string('0', 64),
            downloadBytes: 3,
            installedBytes: 3,
            entryPoint: "ggml-base.bin") with
        {
            MinimumProviderVersion = "9.0.0",
            MaximumProviderVersion = "10.0.0",
        };

        var result = await installer.InstallAsync(descriptor, progress: null, CancellationToken.None);

        result.Code.Should().Be(VoiceModelResultCode.Incompatible);
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task InstallAsync_PreCancelled_CleansTemporaryDirectories()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        using var root = new TemporaryDirectory();
        var paths = new AppDataPaths(root.Path);
        using var client = CreateClient(new StaticHttpHandler([1, 2, 3]));
        var installer = new VoiceModelInstaller(client, paths, store, ["downloads.example.test"]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await installer.InstallAsync(
            Descriptor(VoiceModelArchiveFormat.None, new string('0', 64), 3, 3, "ggml-base.bin"),
            progress: null,
            cancellation.Token);

        result.Code.Should().Be(VoiceModelResultCode.Cancelled);
        Directory.EnumerateFileSystemEntries(paths.ModelDownloadsPath).Should().BeEmpty();
    }

    [Fact]
    public async Task GetStateAsync_IncludesInstalledVersionMissingFromCatalog()
    {
        var catalog = Substitute.For<IVoiceModelCatalogSource>();
        var installer = Substitute.For<IVoiceModelPackageInstaller>();
        var seeds = Substitute.For<ISeedVoiceModelSource>();
        seeds.GetCatalogAsync(Arg.Any<CancellationToken>()).Returns([]);
        var store = Substitute.For<IVoiceModelStore>();
        var rollback = Model("retired-small", isActive: false) with { IsLastKnownGood = true };
        store.GetInstalledAsync(VoiceModelProvider.CommandWhisper, Arg.Any<CancellationToken>())
            .Returns([rollback]);
        store.GetInstalledAsync(VoiceModelProvider.WakeVosk, Arg.Any<CancellationToken>())
            .Returns([]);
        var gate = Substitute.For<IVoiceModelActivationGate>();
        var manager = new VoiceModelManager(catalog, installer, seeds, store, gate);

        var state = await manager.GetStateAsync(CancellationToken.None);

        state.InstalledModels.Should().ContainSingle().Which.Should().Be(rollback);
    }

    [Fact]
    public async Task GetAsync_RedirectToUntrustedOrigin_IsRejectedBeforeFollowingIt()
    {
        var requested = new Uri("https://downloads.example.test/models.manifest.json");
        var redirected = new Uri("https://untrusted.example.test/models.manifest.json");
        var transport = new RedirectHttpHandler(requested, redirected);
        using var client = new SafeVoiceModelHttpClient(
            transport,
            [new Uri("https://downloads.example.test")]);

        var action = () => client.GetAsync(
            requested,
            (_, _) => Task.FromResult(true),
            CancellationToken.None);

        await action.Should().ThrowAsync<VoiceModelCatalogException>()
            .Where(exception => exception.Code == VoiceModelResultCode.InvalidManifest);
        transport.RequestedUris.Should().Equal(requested);
    }

    [Fact]
    public async Task GetAsync_ResponseBodyStalls_OperationTimeoutCancelsRead()
    {
        var requested = new Uri("https://downloads.example.test/model");
        using var client = new SafeVoiceModelHttpClient(
            new StreamingHttpHandler(new BlockingReadStream()),
            [new Uri("https://downloads.example.test")],
            TimeSpan.FromMilliseconds(50));

        var action = () => client.GetAsync(
            requested,
            async (response, operationToken) =>
            {
                await response.Content.ReadAsByteArrayAsync(operationToken);
                return true;
            },
            CancellationToken.None);

        await action.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task InstallLocalAsync_InvalidWhisperHeader_ReturnsInvalidModel()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        using var root = new TemporaryDirectory();
        var paths = new AppDataPaths(root.Path);
        var payload = Encoding.ASCII.GetBytes("bad!-content");
        var payloadPath = Path.Combine(root.Path, "ggml-base.bin");
        await File.WriteAllBytesAsync(payloadPath, payload);
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        using var client = CreateClient(new StaticHttpHandler([]));
        var installer = new VoiceModelInstaller(client, paths, store, ["downloads.example.test"]);

        var result = await installer.InstallLocalAsync(
            Descriptor(VoiceModelArchiveFormat.None, hash, payload.Length, payload.Length, "ggml-base.bin") with { IsBuiltIn = true },
            payloadPath,
            CancellationToken.None);

        result.Code.Should().Be(VoiceModelResultCode.InvalidModel);
        (await store.GetInstalledAsync(VoiceModelProvider.CommandWhisper, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task InstallLocalAsync_LegacyWhisperLittleEndianMagic_IsAccepted()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        using var root = new TemporaryDirectory();
        var paths = new AppDataPaths(root.Path);
        var payload = Encoding.ASCII.GetBytes("lmgg-real-model-content");
        var payloadPath = Path.Combine(root.Path, "ggml-base.bin");
        await File.WriteAllBytesAsync(payloadPath, payload);
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        using var client = CreateClient(new StaticHttpHandler([]));
        var installer = new VoiceModelInstaller(client, paths, store, ["downloads.example.test"]);

        var result = await installer.InstallLocalAsync(
            Descriptor(VoiceModelArchiveFormat.None, hash, payload.Length, payload.Length, "ggml-base.bin"),
            payloadPath,
            CancellationToken.None);

        result.Code.Should().Be(VoiceModelResultCode.Success);
    }

    [Fact]
    public async Task InstallLocalAsync_InvalidVoskStructure_ReturnsInvalidModel()
    {
        var archive = CreateZip(("model/unexpected.txt", "not-a-vosk-model"));
        var hash = Convert.ToHexString(SHA256.HashData(archive));
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        using var root = new TemporaryDirectory();
        var paths = new AppDataPaths(root.Path);
        var payloadPath = Path.Combine(root.Path, "wake.zip");
        await File.WriteAllBytesAsync(payloadPath, archive);
        using var client = CreateClient(new StaticHttpHandler([]));
        var installer = new VoiceModelInstaller(client, paths, store, ["downloads.example.test"]);
        var descriptor = Descriptor(VoiceModelArchiveFormat.Zip, hash, archive.Length, 1024, "model") with
        {
            Id = "wake-ru-small",
            ProviderId = VoiceModelProvider.WakeVosk,
            DisplayName = "Vosk Russian small",
            Version = "0.22",
            IsBuiltIn = true,
        };

        var result = await installer.InstallLocalAsync(descriptor, payloadPath, CancellationToken.None);

        result.Code.Should().Be(VoiceModelResultCode.InvalidModel);
        (await store.GetInstalledAsync(VoiceModelProvider.WakeVosk, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task InstallLocalAsync_EmptyVoskDirectories_ReturnsInvalidModel()
    {
        var archive = CreateZipDirectories("model/am/", "model/conf/");
        var hash = Convert.ToHexString(SHA256.HashData(archive));
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        using var root = new TemporaryDirectory();
        var paths = new AppDataPaths(root.Path);
        var payloadPath = Path.Combine(root.Path, "wake.zip");
        await File.WriteAllBytesAsync(payloadPath, archive);
        using var client = CreateClient(new StaticHttpHandler([]));
        var installer = new VoiceModelInstaller(client, paths, store, ["downloads.example.test"]);
        var descriptor = Descriptor(VoiceModelArchiveFormat.Zip, hash, archive.Length, 1024, "model") with
        {
            Id = "wake-ru-small",
            ProviderId = VoiceModelProvider.WakeVosk,
            DisplayName = "Vosk Russian small",
            Version = "0.22",
        };

        var result = await installer.InstallLocalAsync(descriptor, payloadPath, CancellationToken.None);

        result.Code.Should().Be(VoiceModelResultCode.InvalidModel);
    }

    [Fact]
    public async Task InstallLocalAsync_RequiredVoskFilesArePresent_IsAccepted()
    {
        var archive = CreateZip(
            ("model/am/final.mdl", "model"),
            ("model/conf/mfcc.conf", "mfcc"),
            ("model/conf/model.conf", "config"),
            ("model/graph/Gr.fst", "grammar"),
            ("model/graph/HCLr.fst", "graph"),
            ("model/graph/phones/word_boundary.int", "boundaries"));
        var hash = Convert.ToHexString(SHA256.HashData(archive));
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        using var root = new TemporaryDirectory();
        var paths = new AppDataPaths(root.Path);
        var payloadPath = Path.Combine(root.Path, "wake.zip");
        await File.WriteAllBytesAsync(payloadPath, archive);
        using var client = CreateClient(new StaticHttpHandler([]));
        var installer = new VoiceModelInstaller(client, paths, store, ["downloads.example.test"]);
        var descriptor = Descriptor(VoiceModelArchiveFormat.Zip, hash, archive.Length, 1024, "model") with
        {
            Id = "wake-ru-small",
            ProviderId = VoiceModelProvider.WakeVosk,
            DisplayName = "Vosk Russian small",
            Version = "0.22",
        };

        var result = await installer.InstallLocalAsync(descriptor, payloadPath, CancellationToken.None);

        result.Code.Should().Be(VoiceModelResultCode.Success);
    }

    [Fact]
    public async Task InstallAsync_UnpackedFileExceedsInstalledLimit_ReturnsInvalidManifest()
    {
        var payload = Encoding.ASCII.GetBytes("lmgg-content-over-limit");
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        using var root = new TemporaryDirectory();
        var paths = new AppDataPaths(root.Path);
        using var client = CreateClient(new StaticHttpHandler(payload));
        var installer = new VoiceModelInstaller(client, paths, store, ["downloads.example.test"]);

        var result = await installer.InstallAsync(
            Descriptor(VoiceModelArchiveFormat.None, hash, payload.Length, 4, "ggml-base.bin"),
            progress: null,
            CancellationToken.None);

        result.Code.Should().Be(VoiceModelResultCode.InvalidManifest);
        (await store.GetInstalledAsync(VoiceModelProvider.CommandWhisper, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task InstallAsync_InstalledProgressThrows_KeepsCommittedModel()
    {
        var payload = Encoding.ASCII.GetBytes("lmgg-progress-content");
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        using var root = new TemporaryDirectory();
        var paths = new AppDataPaths(root.Path);
        using var client = CreateClient(new StaticHttpHandler(payload));
        var installer = new VoiceModelInstaller(client, paths, store, ["downloads.example.test"]);

        var result = await installer.InstallAsync(
            Descriptor(VoiceModelArchiveFormat.None, hash, payload.Length, payload.Length, "ggml-base.bin"),
            new ThrowingInstalledProgress(),
            CancellationToken.None);

        result.Code.Should().Be(VoiceModelResultCode.Success);
        var installed = (await store.GetInstalledAsync(VoiceModelProvider.CommandWhisper, CancellationToken.None)).Single();
        Directory.Exists(Path.Combine(paths.ModelsRootPath, installed.RelativePath)).Should().BeTrue();
    }

    [Fact]
    public async Task InstallLocalAsync_ExistingImmutableDirectory_FailsClosed()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        using var root = new TemporaryDirectory();
        var paths = new AppDataPaths(root.Path);
        var payload = Encoding.ASCII.GetBytes("lmgg-new-content");
        var payloadPath = Path.Combine(root.Path, "ggml-base.bin");
        await File.WriteAllBytesAsync(payloadPath, payload);
        var descriptor = Descriptor(
            VoiceModelArchiveFormat.None,
            Convert.ToHexString(SHA256.HashData(payload)),
            payload.Length,
            payload.Length,
            "ggml-base.bin");
        var finalPath = Path.Combine(paths.ModelsRootPath, descriptor.ProviderId.ToString(), descriptor.Id, descriptor.Version);
        Directory.CreateDirectory(finalPath);
        await File.WriteAllTextAsync(Path.Combine(finalPath, descriptor.EntryPoint), "tampered");
        using var client = CreateClient(new StaticHttpHandler([]));
        var installer = new VoiceModelInstaller(client, paths, store, ["downloads.example.test"]);

        var result = await installer.InstallLocalAsync(descriptor, payloadPath, CancellationToken.None);

        result.Code.Should().Be(VoiceModelResultCode.InvalidModel);
        (await store.GetInstalledAsync(descriptor.ProviderId, CancellationToken.None)).Should().BeEmpty();
        (await File.ReadAllTextAsync(Path.Combine(finalPath, descriptor.EntryPoint))).Should().Be("tampered");
    }

    [Fact]
    public async Task InstallLocalAsync_ExistingIdenticalDirectory_ReusesOnlyAfterFullVerification()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        using var root = new TemporaryDirectory();
        var paths = new AppDataPaths(root.Path);
        var payload = Encoding.ASCII.GetBytes("lmgg-identical-content");
        var payloadPath = Path.Combine(root.Path, "ggml-base.bin");
        await File.WriteAllBytesAsync(payloadPath, payload);
        var descriptor = Descriptor(
            VoiceModelArchiveFormat.None,
            Convert.ToHexString(SHA256.HashData(payload)),
            payload.Length,
            payload.Length,
            "ggml-base.bin");
        var finalPath = Path.Combine(paths.ModelsRootPath, descriptor.ProviderId.ToString(), descriptor.Id, descriptor.Version);
        Directory.CreateDirectory(finalPath);
        await File.WriteAllBytesAsync(Path.Combine(finalPath, descriptor.EntryPoint), payload);
        using var client = CreateClient(new StaticHttpHandler([]));
        var installer = new VoiceModelInstaller(client, paths, store, ["downloads.example.test"]);

        var result = await installer.InstallLocalAsync(descriptor, payloadPath, CancellationToken.None);

        result.Code.Should().Be(VoiceModelResultCode.Success);
        (await store.GetInstalledAsync(descriptor.ProviderId, CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task RestoreAsync_RegistrationConflict_RestoresOriginalDirectory()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        using var root = new TemporaryDirectory();
        var paths = new AppDataPaths(root.Path);
        var seedDirectory = Path.Combine(root.Path, "publish-assets");
        Directory.CreateDirectory(seedDirectory);
        var original = Encoding.ASCII.GetBytes("lmgg-original-content");
        var replacement = Encoding.ASCII.GetBytes("lmgg-replacement-content");
        await File.WriteAllBytesAsync(Path.Combine(seedDirectory, "ggml-base.bin"), replacement);
        await WriteSeedManifestAsync(
            seedDirectory,
            Convert.ToHexString(SHA256.HashData(replacement)),
            replacement.Length);
        var relativePath = Path.Combine(
            VoiceModelProvider.CommandWhisper.ToString(),
            "whisper-base-multi",
            "openai-base");
        var finalPath = Path.Combine(paths.ModelsRootPath, relativePath);
        Directory.CreateDirectory(finalPath);
        await File.WriteAllBytesAsync(Path.Combine(finalPath, "ggml-base.bin"), original);
        await store.RegisterAsync(
            Model("openai-base", isActive: true) with
            {
                ModelId = "whisper-base-multi",
                RelativePath = relativePath,
                Sha256 = Convert.ToHexString(SHA256.HashData(original)),
            },
            CancellationToken.None);
        using var client = CreateClient(new StaticHttpHandler([]));
        var installer = new VoiceModelInstaller(client, paths, store, ["downloads.example.test"]);
        var initializer = new SeedVoiceModelInitializer(seedDirectory, installer, store);

        var restored = await initializer.RestoreAsync(VoiceModelProvider.CommandWhisper, CancellationToken.None);

        restored.Result.Code.Should().Be(VoiceModelResultCode.IoFailure);
        (await File.ReadAllBytesAsync(Path.Combine(finalPath, "ggml-base.bin"))).Should().Equal(original);
        Directory.EnumerateDirectories(Path.GetDirectoryName(finalPath)!, "*.rollback-*").Should().BeEmpty();
    }

    [Fact]
    public async Task RestoreAsync_CorruptInstalledSeed_ReplacesContentFromReadOnlyAsset()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new VoiceModelStore(database.Factory);
        using var root = new TemporaryDirectory();
        var paths = new AppDataPaths(root.Path);
        var seedDirectory = Path.Combine(root.Path, "publish-assets");
        Directory.CreateDirectory(seedDirectory);
        var expected = Encoding.ASCII.GetBytes("lmgg-seed-content");
        var hash = Convert.ToHexString(SHA256.HashData(expected));
        await File.WriteAllBytesAsync(Path.Combine(seedDirectory, "ggml-base.bin"), expected);
        await WriteSeedManifestAsync(seedDirectory, hash, expected.Length);
        using var client = CreateClient(new StaticHttpHandler([]));
        var installer = new VoiceModelInstaller(client, paths, store, ["downloads.example.test"]);
        var initializer = new SeedVoiceModelInitializer(seedDirectory, installer, store);
        await initializer.InitializeAsync(CancellationToken.None);
        var active = (await store.GetActiveAsync(VoiceModelProvider.CommandWhisper, CancellationToken.None))!;
        var installedPath = Path.Combine(paths.ModelsRootPath, active.RelativePath, "ggml-base.bin");
        await File.WriteAllTextAsync(installedPath, "corrupt");

        var restored = await initializer.RestoreAsync(VoiceModelProvider.CommandWhisper, CancellationToken.None);

        restored.Result.Code.Should().Be(VoiceModelResultCode.Success);
        (await File.ReadAllBytesAsync(installedPath)).Should().Equal(expected);
    }

    private static InstalledVoiceModel Model(string version, bool isActive) => new(
        VoiceModelProvider.CommandWhisper,
        "whisper-multi",
        version,
        Path.Combine("whisper", "whisper-multi", version),
        new string('a', 64),
        VoiceModelSource.Seed,
        isActive,
        false,
        DateTimeOffset.Parse("2026-07-18T00:00:00+03:00"));

    private static VoiceModelDescriptor Descriptor(
        VoiceModelArchiveFormat archiveFormat,
        string hash,
        long downloadBytes,
        long installedBytes,
        string entryPoint) => new(
            "whisper-base-multi",
            VoiceModelProvider.CommandWhisper,
            "Whisper base multilingual",
            "openai-base",
            "Base",
            new Uri("https://downloads.example.test/model"),
            hash,
            downloadBytes,
            installedBytes,
            entryPoint,
            "MIT",
            false,
            archiveFormat,
            "0.0.0",
            "99.0.0");

    private static async Task WriteSeedManifestAsync(string seedDirectory, string hash, int modelLength)
    {
        var manifest = $$"""
            {
              "schemaVersion": 1,
              "models": [
                {
                  "id": "whisper-base-multi",
                  "provider": "CommandWhisper",
                  "displayName": "Whisper base multilingual",
                  "version": "openai-base",
                  "qualityTier": "Base",
                  "asset": "ggml-base.bin",
                  "sha256": "{{hash}}",
                  "downloadBytes": {{modelLength}},
                  "installedBytes": {{modelLength}},
                  "entryPoint": "ggml-base.bin",
                  "licenseId": "MIT",
                  "archiveFormat": "None",
                  "minimumProviderVersion": "0.0.0",
                  "maximumProviderVersion": "99.0.0"
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(seedDirectory, "seed-manifest.json"), manifest, Encoding.UTF8);
    }

    private static byte[] CreateZip(params (string Name, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(item.Content);
            }
        }

        return output.ToArray();
    }

    private static byte[] CreateZipWithSymbolicLink()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("model/link");
            entry.ExternalAttributes = 0xA000 << 16;
            using var writer = new StreamWriter(entry.Open());
            writer.Write("target");
        }

        return output.ToArray();
    }

    private static byte[] CreateZipDirectories(params string[] directories)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var directory in directories)
            {
                archive.CreateEntry(directory);
            }
        }

        return output.ToArray();
    }

    private static SafeVoiceModelHttpClient CreateClient(HttpMessageHandler handler) => new(
        handler,
        [new Uri("https://downloads.example.test")]);

    private sealed class StaticHttpHandler(byte[] content) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
        }
    }

    private sealed class MappedHttpHandler(IReadOnlyDictionary<Uri, byte[]> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is null || !content.TryGetValue(request.RequestUri, out var bytes))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            });
        }
    }

    private sealed class RedirectHttpHandler(Uri source, Uri target) : HttpMessageHandler
    {
        public List<Uri> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedUris.Add(request.RequestUri!);
            if (request.RequestUri == source)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = target },
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            });
        }
    }

    private sealed class StreamingHttpHandler(Stream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            });
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowingInstalledProgress : IProgress<VoiceModelProgress>
    {
        public void Report(VoiceModelProgress value)
        {
            if (string.Equals(value.Stage, "installed", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("UI callback failure");
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"deskpilot-model-files-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    public sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _path;

        private TestDatabase(string path, TestDbContextFactory factory)
        {
            _path = path;
            Factory = factory;
        }

        public TestDbContextFactory Factory { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"deskpilot-models-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<DeskPilotDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False")
                .Options;
            var factory = new TestDbContextFactory(options);
            await using var context = factory.CreateDbContext();
            await context.Database.MigrateAsync();
            return new TestDatabase(path, factory);
        }

        public ValueTask DisposeAsync()
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            return ValueTask.CompletedTask;
        }
    }

    public sealed class TestDbContextFactory(DbContextOptions<DeskPilotDbContext> options) : IDbContextFactory<DeskPilotDbContext>
    {
        public DeskPilotDbContext CreateDbContext() => new(options);

        public Task<DeskPilotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
