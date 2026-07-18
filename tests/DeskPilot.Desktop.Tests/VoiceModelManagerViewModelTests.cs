using DeskPilot.Desktop.ViewModels;
using DeskPilot.Voice.Abstractions;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DeskPilot.Desktop.Tests;

public sealed class VoiceModelManagerViewModelTests
{
    [Fact]
    public async Task InitializeAsync_DoesNotDownloadWithoutExplicitUserAction()
    {
        var manager = Substitute.For<IVoiceModelManager>();
        manager.GetStateAsync(Arg.Any<CancellationToken>()).Returns(VoiceModelState.Empty);
        var viewModel = new VoiceModelManagerViewModel(manager);

        await viewModel.InitializeAsync();

        await manager.DidNotReceive().InstallAsync(
            Arg.Any<VoiceModelDescriptor>(),
            Arg.Any<IProgress<VoiceModelProgress>?>(),
            Arg.Any<CancellationToken>());
        viewModel.StatusMessage.Should().Be("Модели готовы к проверке.");
    }

    [Fact]
    public async Task CheckForUpdatesCommand_RefreshesCatalogWithoutInstalling()
    {
        var manager = Substitute.For<IVoiceModelManager>();
        var model = Model();
        manager.CheckForUpdatesAsync(Arg.Any<CancellationToken>())
            .Returns(new VoiceModelState([model], new Dictionary<VoiceModelProvider, string>()));
        var viewModel = new VoiceModelManagerViewModel(manager);

        await viewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        viewModel.Models.Should().ContainSingle().Which.Should().Be(model);
        await manager.DidNotReceive().InstallAsync(
            Arg.Any<VoiceModelDescriptor>(),
            Arg.Any<IProgress<VoiceModelProgress>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DownloadOrUpdateCommand_InstallsOnlySelectedModel()
    {
        var manager = Substitute.For<IVoiceModelManager>();
        manager.InstallAsync(
                Arg.Any<VoiceModelDescriptor>(),
                Arg.Any<IProgress<VoiceModelProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new VoiceModelOperationResult(VoiceModelResultCode.Success));
        var model = Model();
        var viewModel = new VoiceModelManagerViewModel(manager) { SelectedModel = model };

        await viewModel.DownloadOrUpdateCommand.ExecuteAsync(null);

        await manager.Received(1).InstallAsync(
            model,
            Arg.Any<IProgress<VoiceModelProgress>?>(),
            Arg.Any<CancellationToken>());
        viewModel.StatusMessage.Should().Be("Модель установлена.");
    }

    [Fact]
    public async Task InitializeAsync_InstalledRollbackMissingFromCatalog_RemainsSelectable()
    {
        var manager = Substitute.For<IVoiceModelManager>();
        var installed = new InstalledVoiceModel(
            VoiceModelProvider.CommandWhisper,
            "whisper-multi",
            "retired-small",
            Path.Combine("CommandWhisper", "whisper-multi", "retired-small"),
            new string('a', 64),
            VoiceModelSource.Download,
            false,
            true,
            DateTimeOffset.Parse("2026-07-18T00:00:00+03:00"));
        manager.GetStateAsync(Arg.Any<CancellationToken>()).Returns(
            VoiceModelState.Empty with { InstalledModels = [installed] });
        var viewModel = new VoiceModelManagerViewModel(manager);

        await viewModel.InitializeAsync();

        viewModel.Models.Should().ContainSingle(model => model.Version == "retired-small");
        viewModel.DownloadOrUpdateCommand.CanExecute(null).Should().BeFalse();
        viewModel.UseModelCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task UseModelCommand_ManagerThrows_ShowsSafeMessageAndReenablesCommands()
    {
        var manager = Substitute.For<IVoiceModelManager>();
        manager.ActivateAsync(
                Arg.Any<VoiceModelProvider>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<VoiceModelOperationResult>>(_ => throw new IOException("private path"));
        var viewModel = new VoiceModelManagerViewModel(manager) { SelectedModel = Model() };

        await viewModel.UseModelCommand.ExecuteAsync(null);

        viewModel.StatusMessage.Should().Be("Не удалось активировать модель.");
        viewModel.UseModelCommand.CanExecute(null).Should().BeTrue();
    }

    private static VoiceModelDescriptor Model() => new(
        "whisper-small-multi",
        VoiceModelProvider.CommandWhisper,
        "Whisper small multilingual",
        "openai-small",
        "HigherAccuracy",
        new Uri("https://downloads.example.test/ggml-small.bin"),
        new string('a', 64),
        100,
        100,
        "ggml-small.bin",
        "MIT",
        false,
        VoiceModelArchiveFormat.None,
        "0.0.0",
        "99.0.0");
}
