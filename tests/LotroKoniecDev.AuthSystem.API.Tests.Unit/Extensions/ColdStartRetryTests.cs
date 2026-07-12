using Microsoft.Extensions.Logging;
using Npgsql;
using NSubstitute;
using LotroKoniecDev.AuthSystem.API.Extensions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Extensions;

public sealed class ColdStartRetryTests
{
    private readonly ILogger _logger = Substitute.For<ILogger>();

    [Fact]
    public async Task ExecuteAsync_SucceedingOperation_InvokesOperationOnce()
    {
        int attempts = 0;

        await ColdStartRetry.ExecuteAsync(
            () =>
            {
                attempts++;
                return Task.CompletedTask;
            },
            _logger,
            delayBetweenAttempts: TimeSpan.Zero);

        attempts.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_TransientFailuresThenSuccess_RetriesUntilSuccess()
    {
        int attempts = 0;

        await ColdStartRetry.ExecuteAsync(
            () =>
            {
                attempts++;
                return attempts < 3
                    ? Task.FromException(CreateTransientNpgsqlException())
                    : Task.CompletedTask;
            },
            _logger,
            delayBetweenAttempts: TimeSpan.Zero);

        attempts.ShouldBe(3);
    }

    [Fact]
    public async Task ExecuteAsync_PersistentTransientFailure_RethrowsAfterMaxAttempts()
    {
        const int maxAttempts = 4;
        int attempts = 0;

        NpgsqlException thrown = await Should.ThrowAsync<NpgsqlException>(() =>
            ColdStartRetry.ExecuteAsync(
                () =>
                {
                    attempts++;
                    return Task.FromException(CreateTransientNpgsqlException());
                },
                _logger,
                maxAttempts,
                TimeSpan.Zero));

        attempts.ShouldBe(maxAttempts);
        thrown.IsTransient.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_NonTransientFailure_DoesNotRetry()
    {
        int attempts = 0;

        await Should.ThrowAsync<InvalidOperationException>(() =>
            ColdStartRetry.ExecuteAsync(
                () =>
                {
                    attempts++;
                    return Task.FromException(new InvalidOperationException("Admin user seeding failed"));
                },
                _logger,
                delayBetweenAttempts: TimeSpan.Zero));

        attempts.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_NonTransientNpgsqlFailure_DoesNotRetry()
    {
        int attempts = 0;

        await Should.ThrowAsync<NpgsqlException>(() =>
            ColdStartRetry.ExecuteAsync(
                () =>
                {
                    attempts++;
                    return Task.FromException(new NpgsqlException("password authentication failed"));
                },
                _logger,
                delayBetweenAttempts: TimeSpan.Zero));

        attempts.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_TransientFailureWrappedInOuterException_RetriesUntilSuccess()
    {
        int attempts = 0;

        await ColdStartRetry.ExecuteAsync(
            () =>
            {
                attempts++;
                return attempts < 2
                    ? Task.FromException(new InvalidOperationException(
                        "Migration failed",
                        CreateTransientNpgsqlException()))
                    : Task.CompletedTask;
            },
            _logger,
            delayBetweenAttempts: TimeSpan.Zero);

        attempts.ShouldBe(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ExecuteAsync_MaxAttemptsBelowOne_ThrowsArgumentOutOfRangeException(int maxAttempts)
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            ColdStartRetry.ExecuteAsync(
                () => Task.CompletedTask,
                _logger,
                maxAttempts,
                TimeSpan.Zero));
    }

    private static NpgsqlException CreateTransientNpgsqlException() =>
        new("The operation has timed out", new TimeoutException());
}
