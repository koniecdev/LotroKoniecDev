using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Application.Features.AppUpdating;
using LotroKoniecDev.DesktopApp.Presentation;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using LotroKoniecDev.DesktopApp.Tests.Unit.Shared;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

public sealed class AppUpdateViewModelTests
{
    private static readonly AvailableAppUpdate Update = new(
        new DesktopVersion(0, 3, 0),
        new Uri("https://github.com/koniecdev/LotroKoniecDev/releases/download/desktop-v0.3.0/LotroPoPolsku-Setup-0.3.0.exe"),
        new Uri("https://github.com/koniecdev/LotroKoniecDev/releases/download/desktop-v0.3.0/LotroPoPolsku-Setup-0.3.0.exe.sha256"));

    private readonly ICommandHandler<InstallAppUpdateCommand, Result> _installHandler =
        Substitute.For<ICommandHandler<InstallAppUpdateCommand, Result>>();

    private readonly IAppShutdown _appShutdown = Substitute.For<IAppShutdown>();
    private readonly IProgress<OperationProgress> _progress = Substitute.For<IProgress<OperationProgress>>();
    private readonly OperationGate _gate = new();

    [Fact]
    public void Constructor_BeforeTheCheck_ShowsNothing()
    {
        // Act
        AppUpdateViewModel viewModel = Create(ViewModelBuilder.DevelopmentBuildCheck());

        // Assert
        viewModel.IsPromptVisible.ShouldBeFalse();
        viewModel.IsStatusVisible.ShouldBeFalse();
        viewModel.AcceptCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task CheckAsync_ANewerVersion_AsksInPolishWithTheVersion()
    {
        // Arrange
        AppUpdateViewModel viewModel = Create(ViewModelBuilder.CheckAnswering(AppUpdateCheckResponse.UpdateAvailable(Update)));

        // Act
        await viewModel.CheckAsync();

        // Assert
        viewModel.IsPromptVisible.ShouldBeTrue();
        viewModel.PromptText.ShouldBe("Dostępna jest wersja 0.3.0. Zaktualizować?");
        viewModel.AcceptCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task CheckAsync_AnyBuild_PassesTheStampedVersion()
    {
        // Arrange
        IQueryHandler<CheckForAppUpdateQuery, Result<AppUpdateCheckResponse>> check = ViewModelBuilder.DevelopmentBuildCheck();
        AppUpdateViewModel viewModel = Create(check, installedVersion: "0.2.7");

        // Act
        await viewModel.CheckAsync();

        // Assert: the query is the only place the version goes.
        await check.Received(1).Handle(new CheckForAppUpdateQuery("0.2.7"), Arg.Any<CancellationToken>());
    }

    public static TheoryData<AppUpdateCheckResponse> AnswersThatShowNothing() =>
    [
        AppUpdateCheckResponse.DevelopmentBuild(),
        AppUpdateCheckResponse.UpToDate()
    ];

    [Theory]
    [MemberData(nameof(AnswersThatShowNothing))]
    public async Task CheckAsync_NothingToOffer_ShowsNothing(AppUpdateCheckResponse response)
    {
        // Arrange
        AppUpdateViewModel viewModel = Create(ViewModelBuilder.CheckAnswering(response));

        // Act
        await viewModel.CheckAsync();

        // Assert
        viewModel.IsPromptVisible.ShouldBeFalse();
        viewModel.IsStatusVisible.ShouldBeFalse();
    }

    [Theory]
    [InlineData(DomainErrors.AppUpdate.NetworkErrorCode, "brak połączenia z GitHubem")]
    [InlineData(DomainErrors.AppUpdate.TimedOutCode, "GitHub nie odpowiedział na czas")]
    [InlineData(DomainErrors.AppUpdate.RateLimitedCode, "GitHub chwilowo ogranicza liczbę zapytań, spróbuj później")]
    [InlineData(DomainErrors.AppUpdate.UnexpectedStatusCode, "GitHub odpowiedział błędem")]
    [InlineData(DomainErrors.AppUpdate.HostRefusedCode, "odpowiedź przyszła spoza GitHuba")]
    [InlineData(DomainErrors.AppUpdate.ResponseTooLargeCode, "odpowiedź GitHuba jest za duża")]
    [InlineData(DomainErrors.AppUpdate.InvalidResponseCode, "GitHub odpowiedział w nieznanym formacie")]
    [InlineData(DomainErrors.AppUpdate.NoDesktopReleaseCode, "na GitHubie nie ma jeszcze wydania aplikacji")]
    [InlineData("Something.Else", "nieoczekiwany błąd")]
    public async Task CheckAsync_AFailedCheck_SaysWhyInPolishAndKeepsTheVersion(string code, string reason)
    {
        // Arrange
        AppUpdateViewModel viewModel = Create(
            ViewModelBuilder.CheckAnswering(AppUpdateCheckResponse.CheckFailed(new Error(code, "english detail"))),
            installedVersion: "0.2.0");

        // Act
        await viewModel.CheckAsync();

        // Assert
        viewModel.IsPromptVisible.ShouldBeFalse();
        viewModel.StatusText.ShouldBe($"Nie udało się sprawdzić aktualizacji: {reason}. Program działa dalej w wersji 0.2.0.");
    }

    [Fact]
    public async Task CheckAsync_AHandlerThatThrows_IsShownAsAFailedCheckInsteadOfEndingTheApp()
    {
        // Arrange
        IQueryHandler<CheckForAppUpdateQuery, Result<AppUpdateCheckResponse>> check =
            Substitute.For<IQueryHandler<CheckForAppUpdateQuery, Result<AppUpdateCheckResponse>>>();
        check.Handle(Arg.Any<CheckForAppUpdateQuery>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<Result<AppUpdateCheckResponse>>>(_ => throw new InvalidOperationException("bug"));
        AppUpdateViewModel viewModel = Create(check, installedVersion: "0.2.0");

        // Act
        await viewModel.CheckAsync();

        // Assert
        viewModel.StatusText.ShouldBe("Nie udało się sprawdzić aktualizacji: nieoczekiwany błąd. Program działa dalej w wersji 0.2.0.");
    }

    [Fact]
    public async Task CheckAsync_ANewerVersion_NeverTakesTheGate()
    {
        // Arrange
        AppUpdateViewModel viewModel = Create(ViewModelBuilder.CheckAnswering(AppUpdateCheckResponse.UpdateAvailable(Update)));

        // Act
        await viewModel.CheckAsync();

        // Assert: Patch and Play must never wait for a check.
        _gate.IsBusy.ShouldBeFalse();
    }

    [Fact]
    public async Task AcceptAsync_ASuccessfulUpdate_InstallsTheOfferedReleaseAndClosesTheApp()
    {
        // Arrange
        _installHandler.Handle(Arg.Any<InstallAppUpdateCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        AppUpdateViewModel viewModel = await CreateOffering();

        // Act
        await viewModel.AcceptAsync();

        // Assert
        await _installHandler.Received(1).Handle(
            new InstallAppUpdateCommand(Update.Version, Update.InstallerUri, Update.ChecksumUri), Arg.Any<CancellationToken>());
        _appShutdown.Received(1).Shutdown();
        viewModel.StatusText.ShouldBe("Uruchomiono instalator. Program zaraz się zamknie.");
    }

    [Fact]
    public async Task AcceptAsync_ASuccessfulUpdate_KeepsTheGateUntilTheAppCloses()
    {
        // Arrange
        _installHandler.Handle(Arg.Any<InstallAppUpdateCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        AppUpdateViewModel viewModel = await CreateOffering();

        // Act
        await viewModel.AcceptAsync();

        // Assert
        _gate.IsBusy.ShouldBeTrue();
        viewModel.AcceptCommand.CanExecute(null).ShouldBeFalse();
        viewModel.DeclineCommand.CanExecute(null).ShouldBeFalse();
    }

    [Theory]
    [InlineData(DomainErrors.AppUpdate.HashMismatchCode, "pobrany instalator nie zgadza się z sumą kontrolną")]
    [InlineData(DomainErrors.AppUpdate.ChecksumFileInvalidCode, "plik z sumą kontrolną jest nieprawidłowy")]
    [InlineData(DomainErrors.AppUpdate.FileSystemErrorCode, "błąd zapisu lub odczytu pliku na dysku (szczegóły w logu programu)")]
    [InlineData(DomainErrors.AppUpdate.InstallerStartCancelledCode, "nie zgodzono się na uruchomienie instalatora")]
    [InlineData(DomainErrors.AppUpdate.InstallerStartFailedCode, "nie udało się uruchomić instalatora")]
    [InlineData(DomainErrors.AppUpdate.HostRefusedCode, "odpowiedź przyszła spoza GitHuba")]
    [InlineData(DomainErrors.AppUpdate.UnexpectedStatusCode, "GitHub odpowiedział błędem")]
    public async Task AcceptAsync_AFailedUpdate_SaysWhyFreesTheGateAndKeepsTheAppRunning(string code, string reason)
    {
        // Arrange
        _installHandler.Handle(Arg.Any<InstallAppUpdateCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(new Error(code, "english detail")));
        AppUpdateViewModel viewModel = await CreateOffering(installedVersion: "0.2.0");

        // Act
        await viewModel.AcceptAsync();

        // Assert
        viewModel.StatusText.ShouldBe($"Aktualizacja się nie udała: {reason}. Program działa dalej w wersji 0.2.0.");
        _gate.IsBusy.ShouldBeFalse();
        _appShutdown.DidNotReceive().Shutdown();
        viewModel.AcceptCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task AcceptAsync_AFailedUpdate_ResetsTheSharedProgressBar()
    {
        // Arrange
        _installHandler.Handle(Arg.Any<InstallAppUpdateCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(DomainErrors.AppUpdate.HashMismatch("LotroPoPolsku-Setup-0.3.0.exe")));
        AppUpdateViewModel viewModel = await CreateOffering();

        // Act
        await viewModel.AcceptAsync();

        // Assert: the bar shows no percent of a download that was refused.
        _progress.Received(1).Report(Arg.Is<OperationProgress>(progress => progress.Current == 0 && !progress.IsCompleted));
    }

    [Fact]
    public async Task AcceptAsync_AnInstallHandlerThatThrows_SaysWhyFreesTheGateAndKeepsTheAppRunning()
    {
        // Arrange
        _installHandler.Handle(Arg.Any<InstallAppUpdateCommand>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<Result>>(_ => throw new InvalidOperationException("bug"));
        AppUpdateViewModel viewModel = await CreateOffering(installedVersion: "0.2.0");

        // Act
        await viewModel.AcceptAsync();

        // Assert
        viewModel.StatusText.ShouldBe("Aktualizacja się nie udała: nieoczekiwany błąd. Program działa dalej w wersji 0.2.0.");
        _gate.IsBusy.ShouldBeFalse();
        _appShutdown.DidNotReceive().Shutdown();
    }

    [Fact]
    public async Task AcceptAsync_WhileAnotherOperationHoldsTheGate_DoesNothing()
    {
        // Arrange
        AppUpdateViewModel viewModel = await CreateOffering();
        _gate.TryEnter();

        // Act
        await viewModel.AcceptAsync();

        // Assert
        viewModel.AcceptCommand.CanExecute(null).ShouldBeFalse();
        await _installHandler.DidNotReceive().Handle(Arg.Any<InstallAppUpdateCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcceptAsync_NothingOffered_DoesNothing()
    {
        // Arrange
        AppUpdateViewModel viewModel = Create(ViewModelBuilder.CheckAnswering(AppUpdateCheckResponse.UpToDate()));
        await viewModel.CheckAsync();

        // Act
        await viewModel.AcceptAsync();

        // Assert
        _gate.IsBusy.ShouldBeFalse();
        await _installHandler.DidNotReceive().Handle(Arg.Any<InstallAppUpdateCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcceptAsync_WhileDownloading_ShowsWhatItDoesAndDisablesBothButtons()
    {
        // Arrange
        TaskCompletionSource<Result> install = new();
        _installHandler.Handle(Arg.Any<InstallAppUpdateCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<Result>(install.Task));
        AppUpdateViewModel viewModel = await CreateOffering();

        // Act
        Task accepting = viewModel.AcceptAsync();

        // Assert
        viewModel.StatusText.ShouldBe("Pobieram wersję 0.3.0…");
        viewModel.AcceptCommand.CanExecute(null).ShouldBeFalse();
        viewModel.DeclineCommand.CanExecute(null).ShouldBeFalse();
        _gate.IsBusy.ShouldBeTrue();

        install.SetResult(Result.Failure(DomainErrors.AppUpdate.InstallerStartCancelled));
        await accepting;
    }

    [Fact]
    public async Task Decline_WhileOffered_HidesTheQuestion()
    {
        // Arrange
        AppUpdateViewModel viewModel = await CreateOffering();

        // Act
        viewModel.Decline();

        // Assert
        viewModel.IsPromptVisible.ShouldBeFalse();
        viewModel.AcceptCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Decline_AfterAFailedInstall_ClearsTheFailure()
    {
        // Arrange
        _installHandler.Handle(Arg.Any<InstallAppUpdateCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(DomainErrors.AppUpdate.TimedOut));
        AppUpdateViewModel viewModel = await CreateOffering();
        await viewModel.AcceptAsync();

        // Act
        viewModel.Decline();

        // Assert
        viewModel.IsPromptVisible.ShouldBeFalse();
        viewModel.StatusText.ShouldBeNull();
    }

    [Fact]
    public async Task AcceptCommand_WhenTheGateChanges_TellsTheButton()
    {
        // Arrange
        AppUpdateViewModel viewModel = await CreateOffering();
        int notices = 0;
        viewModel.AcceptCommand.CanExecuteChanged += (_, _) => notices++;

        // Act
        _gate.TryEnter();
        bool canAcceptWhileBusy = viewModel.AcceptCommand.CanExecute(null);
        _gate.Exit();

        // Assert
        notices.ShouldBe(2);
        canAcceptWhileBusy.ShouldBeFalse();
        viewModel.AcceptCommand.CanExecute(null).ShouldBeTrue();
    }

    private AppUpdateViewModel Create(
        IQueryHandler<CheckForAppUpdateQuery, Result<AppUpdateCheckResponse>> check,
        string installedVersion = "0.2.0") =>
        ViewModelBuilder.AppUpdate(check, _installHandler, _gate, installedVersion, _appShutdown, _progress);

    private async Task<AppUpdateViewModel> CreateOffering(string installedVersion = "0.2.0")
    {
        AppUpdateViewModel viewModel = Create(
            ViewModelBuilder.CheckAnswering(AppUpdateCheckResponse.UpdateAvailable(Update)), installedVersion);
        await viewModel.CheckAsync();
        return viewModel;
    }
}
