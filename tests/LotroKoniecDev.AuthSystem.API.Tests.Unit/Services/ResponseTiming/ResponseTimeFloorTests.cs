using Microsoft.Extensions.Time.Testing;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.ResponseTiming;

/// <summary>
/// The floor is a lower bound on the answer's time (ADR-0059): never sooner, also when the work throws,
/// and no longer than the rest of the floor once the work used part of it.
/// </summary>
public sealed class ResponseTimeFloorTests
{
    private static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _clock = new();

    [Fact]
    public void HoldAsync_ShouldNotAnswer_BeforeTheFloorHasPassed()
    {
        // Arrange
        ResponseTimeFloor sut = new(_clock);

        // Act
        Task<string> holding = sut.HoldAsync(Floor, () => Task.FromResult("answer"));
        _clock.Advance(Floor - TimeSpan.FromMilliseconds(1));

        // Assert
        holding.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task HoldAsync_ShouldReturnTheWorksResult_OnceTheFloorHasPassed()
    {
        // Arrange
        ResponseTimeFloor sut = new(_clock);

        // Act
        Task<string> holding = sut.HoldAsync(Floor, () => Task.FromResult("answer"));
        _clock.Advance(Floor);

        // Assert
        (await holding.WaitAsync(CompletionTimeout)).ShouldBe("answer");
    }

    [Fact]
    public async Task HoldAsync_ShouldWaitOnlyTheRest_WhenTheWorkUsedPartOfTheFloor()
    {
        // Arrange
        ResponseTimeFloor sut = new(_clock);

        // Act: the work itself takes 300 ms of the 500
        Task<string> holding = sut.HoldAsync(Floor, () => WorkThatTakes(TimeSpan.FromMilliseconds(300)));
        _clock.Advance(TimeSpan.FromMilliseconds(199));
        bool answeredEarly = holding.IsCompleted;
        _clock.Advance(TimeSpan.FromMilliseconds(1));

        // Assert
        answeredEarly.ShouldBeFalse();
        (await holding.WaitAsync(CompletionTimeout)).ShouldBe("answer");
    }

    [Fact]
    public async Task HoldAsync_ShouldAnswerAtOnce_WhenTheWorkRanPastTheFloor()
    {
        // Arrange
        ResponseTimeFloor sut = new(_clock);

        // Act: a branch that ran longer than the floor, such as a slow SMTP relay
        Task<string> holding = sut.HoldAsync(Floor, () => WorkThatTakes(TimeSpan.FromSeconds(2)));

        // Assert
        holding.IsCompletedSuccessfully.ShouldBeTrue();
        (await holding).ShouldBe("answer");
    }

    [Fact]
    public async Task HoldAsync_ShouldWaitForTheFloorAndKeepTheException_WhenTheWorkThrows()
    {
        // Arrange
        ResponseTimeFloor sut = new(_clock);

        // Act
        Task<string> holding = sut.HoldAsync<string>(
            Floor,
            () => Task.FromException<string>(new InvalidOperationException("The database is down.")));
        bool answeredBeforeTheFloor = holding.IsCompleted;
        _clock.Advance(Floor);

        // Assert
        answeredBeforeTheFloor.ShouldBeFalse();
        InvalidOperationException exception =
            await Should.ThrowAsync<InvalidOperationException>(() => holding.WaitAsync(CompletionTimeout));
        exception.Message.ShouldBe("The database is down.");
    }

    [Fact]
    public async Task HoldAsync_ShouldAnswerAtOnce_WhenTheResultMaySkipTheWait()
    {
        // Arrange: the login page lets a verified password skip the floor
        ResponseTimeFloor sut = new(_clock);

        // Act
        Task<string> holding = sut.HoldAsync(Floor, () => Task.FromResult("verified"), skipWaitFor: result => result == "verified");

        // Assert
        holding.IsCompletedSuccessfully.ShouldBeTrue();
        (await holding).ShouldBe("verified");
    }

    [Fact]
    public void HoldAsync_ShouldStillWait_WhenTheResultMayNotSkipTheWait()
    {
        // Arrange
        ResponseTimeFloor sut = new(_clock);

        // Act
        Task<string> holding = sut.HoldAsync(Floor, () => Task.FromResult("failed"), skipWaitFor: result => result == "verified");

        // Assert
        holding.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task HoldAsync_ShouldWaitForTheFloor_WhenTheWorkReturnsNothing()
    {
        // Arrange
        ResponseTimeFloor sut = new(_clock);
        bool workRan = false;

        // Act
        Task holding = sut.HoldAsync(Floor, () =>
        {
            workRan = true;
            return Task.CompletedTask;
        });
        bool answeredBeforeTheFloor = holding.IsCompleted;
        _clock.Advance(Floor);
        await holding.WaitAsync(CompletionTimeout);

        // Assert
        workRan.ShouldBeTrue();
        answeredBeforeTheFloor.ShouldBeFalse();
    }

    [Fact]
    public async Task HoldAsync_ShouldRefuseANegativeFloor()
    {
        // Arrange
        ResponseTimeFloor sut = new(_clock);

        // Act & Assert
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => sut.HoldAsync(TimeSpan.FromMilliseconds(-1), () => Task.FromResult("answer")));
    }

    /// <summary>
    /// Moves the fake clock by the time the work "takes" and finishes at once, so the floor sees the
    /// elapsed time without a real delay.
    /// </summary>
    private Task<string> WorkThatTakes(TimeSpan duration)
    {
        _clock.Advance(duration);
        return Task.FromResult("answer");
    }
}
