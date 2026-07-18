using DeskPilot.Desktop.Services;
using DeskPilot.Infrastructure.Data;
using DeskPilot.Voice.Abstractions;
using DeskPilot.Voice.Vosk;
using DeskPilot.Voice.WhisperCpp;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DeskPilot.Desktop.Tests;

public sealed class LocalVoiceRuntimeProviderFactoryTests
{
    [Fact]
    public void Create_ResolvesActiveModelsInsideLocalModelsRoot()
    {
        using var directory = new TestDirectory();
        var wakeVersionDirectory = directory.CreateDirectory(Path.Combine("WakeVosk", "wake-ru", "0.22"));
        var wakeDirectory = directory.CreateVoskModel(Path.Combine(
            "WakeVosk",
            "wake-ru",
            "0.22",
            "vosk-model-small-ru-0.22"));
        var commandDirectory = directory.CreateDirectory(Path.Combine("CommandWhisper", "whisper-base", "openai-base"));
        var commandModelPath = Path.Combine(commandDirectory, "ggml-base.bin");
        File.WriteAllBytes(commandModelPath, [0x67, 0x67, 0x6d, 0x6c]);
        var paths = Substitute.For<IAppDataPaths>();
        paths.ModelsRootPath.Returns(directory.Path);
        string? receivedWakePath = null;
        string? receivedCommandPath = null;
        var wake = Substitute.For<IWakeWordProvider>();
        var speech = Substitute.For<ISpeechToTextProvider>();
        VoskWakeWordProviderFactory wakeFactory = path =>
        {
            receivedWakePath = path;
            return wake;
        };
        WhisperSpeechToTextProviderFactory commandFactory = path =>
        {
            receivedCommandPath = path;
            return speech;
        };
        var factory = new LocalVoiceRuntimeProviderFactory(paths, wakeFactory, commandFactory);

        var result = factory.Create(
            Model(VoiceModelProvider.WakeVosk, Path.GetRelativePath(directory.Path, wakeVersionDirectory)),
            Model(VoiceModelProvider.CommandWhisper, Path.GetRelativePath(directory.Path, commandDirectory)));

        result.WakeWord.Should().BeSameAs(wake);
        result.SpeechToText.Should().BeSameAs(speech);
        receivedWakePath.Should().Be(Path.GetFullPath(wakeDirectory));
        receivedCommandPath.Should().Be(Path.GetFullPath(commandModelPath));
    }

    [Fact]
    public void Create_RelativePathEscapesModelsRoot_IsRejectedWithoutLeakingPath()
    {
        using var directory = new TestDirectory();
        var paths = Substitute.For<IAppDataPaths>();
        paths.ModelsRootPath.Returns(directory.Path);
        var factory = new LocalVoiceRuntimeProviderFactory(
            paths,
            _ => Substitute.For<IWakeWordProvider>(),
            _ => Substitute.For<ISpeechToTextProvider>());

        var action = () => factory.Create(
            Model(VoiceModelProvider.WakeVosk, Path.Combine("..", "private-model")),
            Model(VoiceModelProvider.CommandWhisper, "command"));

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("Active voice model is unavailable.")
            .Which.Message.Should().NotContain("private-model");
    }

    private static InstalledVoiceModel Model(VoiceModelProvider provider, string relativePath) => new(
        provider,
        provider == VoiceModelProvider.WakeVosk ? "wake-ru" : "whisper-base",
        provider == VoiceModelProvider.WakeVosk ? "0.22" : "openai-base",
        relativePath,
        new string('a', 64),
        VoiceModelSource.Seed,
        true,
        false,
        DateTimeOffset.Parse("2026-07-18T00:00:00+03:00"));

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"deskpilot-runtime-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateDirectory(string relativePath)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateVoskModel(string relativePath)
        {
            var modelPath = CreateDirectory(relativePath);
            foreach (var relativeFile in new[]
                     {
                         System.IO.Path.Combine("am", "final.mdl"),
                         System.IO.Path.Combine("conf", "mfcc.conf"),
                         System.IO.Path.Combine("conf", "model.conf"),
                         System.IO.Path.Combine("graph", "Gr.fst"),
                         System.IO.Path.Combine("graph", "HCLr.fst"),
                         System.IO.Path.Combine("graph", "phones", "word_boundary.int"),
                     })
            {
                var filePath = System.IO.Path.Combine(modelPath, relativeFile);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath)!);
                File.WriteAllText(filePath, "test");
            }

            return modelPath;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
