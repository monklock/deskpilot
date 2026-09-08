using DeskPilot.Desktop.Services;
using DeskPilot.Infrastructure.Data;
using DeskPilot.Voice.Abstractions;
using DeskPilot.Voice.GigaStt;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DeskPilot.Desktop.Tests;

public sealed class LocalVoiceRuntimeProviderFactoryTests
{
    [Fact]
    public void Create_UsesOneLocalBundleForBothProvidersWithoutStartingServer()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "GigaStt", "rnnt", "v1"));
        using var runtime = new GigaSttRuntime();
        var factory = CreateFactory(directory.Path, runtime);

        var result = factory.Create(Model(VoiceModelProvider.GigaStt, Path.Combine("GigaStt", "rnnt", "v1")));

        result.WakeWord.Should().BeOfType<GigaSttWakeWordProvider>();
        result.SpeechToText.Should().BeOfType<GigaSttSpeechToTextProvider>();
    }

    [Theory]
    [InlineData("../outside-model")]
    [InlineData("missing")]
    [InlineData("")]
    [InlineData(".")]
    public void Create_InvalidPath_IsRejectedWithoutLeakingPath(string relativePath)
    {
        using var directory = new TestDirectory();
        using var runtime = new GigaSttRuntime();
        var factory = CreateFactory(directory.Path, runtime);

        var action = () => factory.Create(Model(VoiceModelProvider.GigaStt, relativePath));

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("Active voice model is unavailable.");
    }

    [Theory]
    [InlineData(VoiceModelProvider.WakeVosk)]
    [InlineData(VoiceModelProvider.CommandWhisper)]
    public void Create_LegacyProvider_IsRejected(VoiceModelProvider provider)
    {
        using var directory = new TestDirectory();
        using var runtime = new GigaSttRuntime();
        var factory = CreateFactory(directory.Path, runtime);

        var action = () => factory.Create(Model(provider, "v1"));

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("Active voice model is unavailable.");
    }

    [Fact]
    public async Task StopAsync_BeforeStarting_IsSafeToRepeat()
    {
        using var directory = new TestDirectory();
        using var runtime = new GigaSttRuntime();
        var factory = CreateFactory(directory.Path, runtime);

        await factory.StopAsync(CancellationToken.None);
        await factory.StopAsync(CancellationToken.None);
    }

    private static LocalVoiceRuntimeProviderFactory CreateFactory(string root, GigaSttRuntime runtime)
    {
        var paths = Substitute.For<IAppDataPaths>();
        paths.ModelsRootPath.Returns(root);
        return new LocalVoiceRuntimeProviderFactory(paths, runtime);
    }

    private static InstalledVoiceModel Model(VoiceModelProvider provider, string relativePath) => new(
        provider, "gigastt-rnnt", "v1", relativePath, new string('a', 64),
        VoiceModelSource.Seed, true, false, DateTimeOffset.UtcNow);

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"deskpilot-runtime-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
