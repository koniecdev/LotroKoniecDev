using LotroKoniecDev.DesktopApp.Presentation;
using LotroKoniecDev.DesktopApp.Tests.Unit.Shared;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

public sealed class RunningCopyClaimTests
{
    [Theory]
    [InlineData(RunningMutexState.Created, RunningMutexState.Created, false)]
    [InlineData(RunningMutexState.Created, RunningMutexState.AlreadyExists, false)]
    [InlineData(RunningMutexState.Created, RunningMutexState.Refused, false)]
    [InlineData(RunningMutexState.AlreadyExists, RunningMutexState.Created, true)]
    [InlineData(RunningMutexState.AlreadyExists, RunningMutexState.AlreadyExists, true)]
    [InlineData(RunningMutexState.AlreadyExists, RunningMutexState.Refused, true)]
    [InlineData(RunningMutexState.Refused, RunningMutexState.Created, false)]
    [InlineData(RunningMutexState.Refused, RunningMutexState.AlreadyExists, true)]
    [InlineData(RunningMutexState.Refused, RunningMutexState.Refused, false)]
    public void Create_EveryAnswerFromWindows_RefusesOnlyWhenAnotherCopyHoldsTheDecidingName(
        RunningMutexState global,
        RunningMutexState local,
        bool expectedRefused)
    {
        // Arrange
        FakeRunningMutexOpener opener = new(global, local);

        // Act
        using RunningCopyClaim claim = RunningCopyClaim.Create(opener);

        // Assert
        claim.IsRefused.ShouldBe(expectedRefused);
    }

    [Theory]
    [InlineData(RunningMutexState.AlreadyExists, RunningMutexState.Created)]
    [InlineData(RunningMutexState.AlreadyExists, RunningMutexState.AlreadyExists)]
    [InlineData(RunningMutexState.Refused, RunningMutexState.AlreadyExists)]
    public void Create_ARefusedCopy_HasClosedEveryHandleBeforeItShowsItsMessage(
        RunningMutexState global,
        RunningMutexState local)
    {
        // Arrange
        FakeRunningMutexOpener opener = new(global, local);

        // Act
        using RunningCopyClaim claim = RunningCopyClaim.Create(opener);

        // Assert: an open handle would keep the name alive after the first copy ends.
        opener.Handles.ShouldNotBeEmpty();
        opener.Handles.ShouldAllBe(handle => handle.IsDisposed);
        claim.GlobalHeld.ShouldBeFalse();
        claim.LocalHeld.ShouldBeFalse();
    }

    [Fact]
    public void Create_TheFirstCopy_HoldsBothNamesUntilItEnds()
    {
        // Arrange
        FakeRunningMutexOpener opener = new(RunningMutexState.Created, RunningMutexState.Created);
        RunningCopyClaim claim = RunningCopyClaim.Create(opener);

        // Act
        bool openWhileRunning = opener.Handles.All(handle => !handle.IsDisposed);
        claim.Dispose();

        // Assert
        openWhileRunning.ShouldBeTrue();
        opener.Handles.Count.ShouldBe(2);
        opener.Handles.ShouldAllBe(handle => handle.IsDisposed);
    }

    [Theory]
    [InlineData(RunningMutexState.Created, RunningMutexState.Created, true, true)]
    [InlineData(RunningMutexState.Refused, RunningMutexState.Created, false, true)]
    [InlineData(RunningMutexState.Created, RunningMutexState.Refused, true, false)]
    [InlineData(RunningMutexState.Refused, RunningMutexState.Refused, false, false)]
    public void Create_AStartedCopy_TellsWhichNamesItHolds(
        RunningMutexState global,
        RunningMutexState local,
        bool expectedGlobalHeld,
        bool expectedLocalHeld)
    {
        // Arrange
        FakeRunningMutexOpener opener = new(global, local);

        // Act
        using RunningCopyClaim claim = RunningCopyClaim.Create(opener);

        // Assert: the uninstaller can ask to close this copy only through a name it holds.
        claim.IsRefused.ShouldBeFalse();
        claim.GlobalHeld.ShouldBe(expectedGlobalHeld);
        claim.LocalHeld.ShouldBe(expectedLocalHeld);
    }
}
