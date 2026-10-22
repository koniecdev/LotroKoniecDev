using System.Data.Common;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// No writer wakes the relay by hand: every commit that carries a new outbox row does it, and nothing
/// else does (#781, ADR-0038 §6). The tests run on a host with no relay, so a wake-up stays in the
/// signal until the test takes it. Every test that expects a wake-up fails when the interceptor is not
/// registered.
/// </summary>
public sealed class OutboxWakeUpTests : EndpointsTestBase
{
    private readonly IServiceProvider _hostServices;
    private readonly OutboxSignal _outboxSignal;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        // A wake-up an earlier test left behind would pass for one this test caused.
        await TakeWakeUpAsync();
    }

    [Fact]
    public async Task SaveChanges_WithNoTransactionOpen_WakesTheRelay()
    {
        // Arrange
        await using AsyncServiceScope scope = _hostServices.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        EnqueueRow(scope);

        // Act
        await db.SaveChangesAsync();

        // Assert
        (await TakeWakeUpAsync()).ShouldBeTrue();
    }

    /// <summary>
    /// The <c>RegisterUser</c> trap of ADR-0035: the relay reads committed rows, so a wake-up sent at the
    /// save could find nothing.
    /// </summary>
    [Fact]
    public async Task Commit_AfterASaveWithAnOutboxRow_WakesTheRelayOnlyThen()
    {
        // Arrange
        await using AsyncServiceScope scope = _hostServices.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        bool wokenBeforeTheCommit = true;

        // Act
        await InTransactionAsync(db, async transaction =>
        {
            EnqueueRow(scope);
            await db.SaveChangesAsync();
            wokenBeforeTheCommit = await TakeWakeUpAsync();

            await transaction.CommitAsync();
        });

        // Assert
        wokenBeforeTheCommit.ShouldBeFalse();
        (await TakeWakeUpAsync()).ShouldBeTrue();
    }

    /// <summary>
    /// EF Core reports no event when a transaction is disposed without a commit, so the next transaction
    /// on the same context must not inherit the wake-up the first one left behind.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transaction_EndedWithoutACommit_WakesNothing_EvenWhenTheNextTransactionCommits(
        bool rolledBack)
    {
        // Arrange
        await using AsyncServiceScope scope = _hostServices.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        // Act
        await InTransactionAsync(db, async transaction =>
        {
            EnqueueRow(scope);
            await db.SaveChangesAsync();

            if (rolledBack)
            {
                await transaction.RollbackAsync();
            }
        });
        db.ChangeTracker.Clear();
        await InTransactionAsync(db, transaction => transaction.CommitAsync());

        // Assert
        (await TakeWakeUpAsync()).ShouldBeFalse();
        (await db.OutboxMessages.CountAsync()).ShouldBe(0);
    }

    /// <summary>
    /// A save that fails may have landed with only its answer lost (ADR-0065 §1). A needless wake-up
    /// finds nothing to send.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveChanges_ThatFailsWithNoTransactionOpen_StillWakesTheRelay(bool rowLanded)
    {
        // Arrange
        await using AsyncServiceScope scope = _hostServices.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        EnqueueRow(scope);
        if (rowLanded)
        {
            Factory.DbCommandFailures.FailNextAfterItRuns(IsOutboxInsert, CreateFailure);
        }
        else
        {
            Factory.DbCommandFailures.FailNext(IsOutboxInsert, CreateFailure);
        }

        // Act
        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());

        // Assert
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);
        (await TakeWakeUpAsync()).ShouldBeTrue();
    }

    /// <summary>
    /// Only the end of the transaction says whether anything landed, so the wake-up waits for it.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task SaveChanges_ThatFailsInsideATransaction_WaitsForTheTransactionToEnd(
        bool committed,
        bool expectedWakeUp)
    {
        // Arrange
        await using AsyncServiceScope scope = _hostServices.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Factory.DbCommandFailures.FailNext(IsOutboxInsert, CreateFailure);
        bool wokenBeforeTheEnd = true;

        // Act
        await InTransactionAsync(db, async transaction =>
        {
            EnqueueRow(scope);
            await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
            wokenBeforeTheEnd = await TakeWakeUpAsync();

            if (committed)
            {
                await transaction.CommitAsync();
            }
            else
            {
                await transaction.RollbackAsync();
            }
        });

        // Assert
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);
        wokenBeforeTheEnd.ShouldBeFalse();
        (await TakeWakeUpAsync()).ShouldBe(expectedWakeUp);
    }

    /// <summary>
    /// The commit lands and its answer is lost. The failure injector runs before the wake-up interceptor,
    /// so the interceptor sees only the failed commit, never the successful one.
    /// </summary>
    [Fact]
    public async Task Commit_ThatFails_StillWakesTheRelay()
    {
        // Arrange
        await using AsyncServiceScope scope = _hostServices.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Factory.DbCommitFailures.FailNextCommitAfterItLands(
            eventData => eventData.Context == db,
            CreateFailure);

        // Act
        await Should.ThrowAsync<InvalidOperationException>(() => InTransactionAsync(db, async transaction =>
        {
            EnqueueRow(scope);
            await db.SaveChangesAsync();

            await transaction.CommitAsync();
        }));

        // Assert
        Factory.DbCommitFailures.FailuresInjected.ShouldBe(1);
        (await TakeWakeUpAsync()).ShouldBeTrue();
    }

    /// <summary>
    /// The relay's own save marks rows sent. If that woke the relay, it would wake itself after every pass.
    /// </summary>
    [Fact]
    public async Task SaveChanges_ThatOnlyMarksAnOutboxRowSent_WakesNothing()
    {
        // Arrange
        await using AsyncServiceScope scope = _hostServices.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        EnqueueRow(scope);
        await db.SaveChangesAsync();
        await TakeWakeUpAsync();

        OutboxMessage row = await db.OutboxMessages.SingleAsync();
        row.MarkAsProcessed(DateTimeOffset.UtcNow);

        // Act
        await db.SaveChangesAsync();

        // Assert
        (await TakeWakeUpAsync()).ShouldBeFalse();
    }

    public OutboxWakeUpTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
        WebApplicationFactory<Program> host = appFactory.GetHostWithoutOutboxRelay();
        _hostServices = host.Services;
        _outboxSignal = host.Services.GetRequiredService<OutboxSignal>();
    }

    private static void EnqueueRow(AsyncServiceScope scope)
    {
        OutboxWriter writer = scope.ServiceProvider.GetRequiredService<OutboxWriter>();
        writer.Enqueue(new PasswordResetRequested(Guid.CreateVersion7()));
    }

    /// <summary>
    /// The context retries on failure, so EF accepts a transaction it did not start only inside an
    /// execution strategy, the way <c>RegisterUser</c> runs its own.
    /// </summary>
    private static Task InTransactionAsync(AuthDbContext db, Func<IDbContextTransaction, Task> work)
    {
        IExecutionStrategy executionStrategy = db.Database.CreateExecutionStrategy();
        return executionStrategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync();
            await work(transaction);
        });
    }

    private static bool IsOutboxInsert(DbCommand command) =>
        command.CommandText.Contains("INSERT INTO", StringComparison.Ordinal)
        && command.CommandText.Contains("\"OutboxMessages\"", StringComparison.Ordinal);

    /// <summary>
    /// Not a transient error, so the execution strategy does not replay the save.
    /// </summary>
    private static InvalidOperationException CreateFailure() => new("Injected by OutboxWakeUpTests.");

    /// <summary>
    /// Takes the wake-up the signal holds, if there is one, without waiting.
    /// </summary>
    private Task<bool> TakeWakeUpAsync() =>
        _outboxSignal.WaitAsync(TimeSpan.Zero, TimeProvider.System, CancellationToken.None);
}
