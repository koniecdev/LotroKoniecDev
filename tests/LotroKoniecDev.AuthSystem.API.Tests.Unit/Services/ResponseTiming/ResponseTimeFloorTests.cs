using Microsoft.Extensions.Time.Testing;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.ResponseTiming;

/// <summary>
/// The floor is a lower bound on the answer's time (ADR-0059): never sooner, and no longer than the rest
/// of the floor once part of it has passed.
/// </summary>
public sealed class ResponseTimeFloorTests
{
    private static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void WaitForFloorAsync_ShouldNotComplete_BeforeTheFloorHasPassed()
    {
        // Arrange
        FakeTimeProvider clock = new();
        ResponseTimer timer = new ResponseTimeFloor(clock).Start(Floor);

        // Act
        Task wait = timer.WaitForFloorAsync(CancellationToken.None);
        clock.Advance(Floor - TimeSpan.FromMilliseconds(1));

        // Assert
        wait.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task WaitForFloorAsync_ShouldComplete_OnceTheFloorHasPassed()
    {
        // Arrange
        FakeTimeProvider clock = new();
        ResponseTimer timer = new ResponseTimeFloor(clock).Start(Floor);

        // Act
        Task wait = timer.WaitForFloorAsync(CancellationToken.None);
        clock.Advance(Floor);

        // Assert
        await wait.WaitAsync(CompletionTimeout);
        wait.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task WaitForFloorAsync_ShouldWaitOnlyTheRest_WhenPartOfTheFloorHasPassed()
    {
        // Arrange: the branch itself took 300 ms of the 500
        FakeTimeProvider clock = new();
        ResponseTimer timer = new ResponseTimeFloor(clock).Start(Floor);
        clock.Advance(TimeSpan.FromMilliseconds(300));

        // Act
        Task wait = timer.WaitForFloorAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(199));
        bool completedEarly = wait.IsCompleted;
        clock.Advance(TimeSpan.FromMilliseconds(1));

        // Assert
        completedEarly.ShouldBeFalse();
        await wait.WaitAsync(CompletionTimeout);
        wait.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(500, 500)]
    [InlineData(500, 2000)]
    public void WaitForFloorAsync_ShouldCompleteAtOnce_WhenTheFloorHasAlreadyPassed(
        int floorMilliseconds,
        int elapsedMilliseconds)
    {
        // Arrange: a branch that ran as long as the floor or longer, such as a slow SMTP relay
        FakeTimeProvider clock = new();
        ResponseTimer timer = new ResponseTimeFloor(clock).Start(TimeSpan.FromMilliseconds(floorMilliseconds));
        clock.Advance(TimeSpan.FromMilliseconds(elapsedMilliseconds));

        // Act
        Task wait = timer.WaitForFloorAsync(CancellationToken.None);

        // Assert
        wait.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task WaitForFloorAsync_ShouldStopWaiting_WhenTheCallerHangsUp()
    {
        // Arrange
        FakeTimeProvider clock = new();
        ResponseTimer timer = new ResponseTimeFloor(clock).Start(Floor);
        using CancellationTokenSource requestAborted = new();

        // Act
        Task wait = timer.WaitForFloorAsync(requestAborted.Token);
        await requestAborted.CancelAsync();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(() => wait.WaitAsync(CompletionTimeout));
    }

    [Fact]
    public void Start_ShouldRefuseANegativeFloor()
    {
        // Arrange
        ResponseTimeFloor floor = new(new FakeTimeProvider());

        // Act & Assert
        Should.Throw<ArgumentOutOfRangeException>(() => floor.Start(TimeSpan.FromMilliseconds(-1)));
    }
}
