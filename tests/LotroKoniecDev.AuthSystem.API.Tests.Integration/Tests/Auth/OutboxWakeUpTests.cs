using System.Data.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// Every commit that carries a new outbox row wakes the relay, and so do a failed or cancelled save with no
/// transaction open and a failed commit, because their row may have landed (#781, ADR-0038 §6). Nothing
/// else does. The host runs no relay, so a wake-up stays in the signal until the test takes it.
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
    /// A request aborted while its save was on the way may still have written the row.
    /// </summary>
    [Fact]
    public async Task SaveChanges_CancelledWithNoTransactionOpen_StillWakesTheRelay()
    {
        // Arrange
        await using AsyncServiceScope scope = _hostServices.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        EnqueueRow(scope);
        Factory.DbCommandFailures.FailNextAfterItRuns(IsOutboxInsert, () => new OperationCanceledException());

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => db.SaveChangesAsync());

        // Assert
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);
        (await TakeWakeUpAsync()).ShouldBeTrue();
    }

    /// <summary>
    /// Identity turns a lost concurrency check into a failed result (DeleteAccount, ConfirmEmailChange, the
    /// erasure), and EF Core reports it to an interceptor on its own path, not as a failed save.
    /// </summary>
    [Fact]
    public async Task SaveChanges_ThatLosesItsConcurrencyCheck_StillWakesTheRelay()
    {
        // Arrange
        (_, IdentityId identityId) = await UserFactory.RegisterRandomUserUnconfirmedAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy);
        await using AsyncServiceScope scope = _hostServices.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser staleUser = (await userManager.FindByIdAsync(identityId.Value.ToString())).ShouldNotBeNull();
        await ChangeTheUserInAnotherScopeAsync(identityId.Value);
        EnqueueRow(scope);

        // Act
        IdentityResult result = await userManager.UpdateAsync(staleUser);

        // Assert
        result.Errors.ShouldContain(error => error.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure));
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
    /// A save refused before EF Core begins it reports no end. Its wake-up must not wait for the next save,
    /// which inside a transaction comes before the commit.
    /// </summary>
    [Fact]
    public async Task Commit_AfterASaveThatWasRefusedBeforeItBegan_IsStillTheOnlyWakeUp()
    {
        // Arrange
        SaveRefusal saveRefusal = new();
        await using WebApplicationFactory<Program> host = Factory.GetHostWithoutOutboxRelay()
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.ConfigureDbContext<AuthDbContext>(options => options.AddInterceptors(saveRefusal))));
        OutboxSignal outboxSignal = host.Services.GetRequiredService<OutboxSignal>();
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        EnqueueRow(scope);
        saveRefusal.RefuseNextSave();
        await Should.ThrowAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        bool wokenBeforeTheCommit = true;

        // Act
        await InTransactionAsync(db, async transaction =>
        {
            EnqueueRow(scope);
            await db.SaveChangesAsync();
            wokenBeforeTheCommit = await TakeWakeUpAsync(outboxSignal);

            await transaction.CommitAsync();
        });

        // Assert
        wokenBeforeTheCommit.ShouldBeFalse();
        (await TakeWakeUpAsync(outboxSignal)).ShouldBeTrue();
    }

    /// <summary>
    /// The relay's own saves only update rows; waking on them, the relay would wake itself after every pass.
    /// A failed attempt changes the row every time, so the save below is never empty.
    /// </summary>
    [Fact]
    public async Task SaveChanges_ThatOnlyUpdatesAnOutboxRow_WakesNothing()
    {
        // Arrange
        await using AsyncServiceScope scope = _hostServices.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        EnqueueRow(scope);
        await db.SaveChangesAsync();
        await TakeWakeUpAsync();

        OutboxMessage row = await db.OutboxMessages.SingleAsync();
        row.MarkFailed("broker down");

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

    private async Task ChangeTheUserInAnotherScopeAsync(Guid userId)
    {
        await using AsyncServiceScope scope = _hostServices.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = (await userManager.FindByIdAsync(userId.ToString())).ShouldNotBeNull();
        (await userManager.UpdateSecurityStampAsync(user)).Succeeded.ShouldBeTrue();
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

    private Task<bool> TakeWakeUpAsync() => TakeWakeUpAsync(_outboxSignal);

    /// <summary>
    /// Never waits: answers at once whether the signal held a wake-up.
    /// </summary>
    private static Task<bool> TakeWakeUpAsync(OutboxSignal outboxSignal) =>
        outboxSignal.WaitAsync(TimeSpan.Zero, TimeProvider.System, CancellationToken.None);

    /// <summary>
    /// Sits after the wake-up interceptor and, once armed, refuses the next save before EF Core begins it.
    /// It waits to be armed because the host saves its seed data at startup.
    /// </summary>
    private sealed class SaveRefusal : ISaveChangesInterceptor
    {
        private int _armed;

        public void RefuseNextSave()
        {
            Interlocked.Exchange(ref _armed, 1);
        }

        public ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            Interlocked.Exchange(ref _armed, 0) == 1
                ? throw new InvalidOperationException("Refused by OutboxWakeUpTests.")
                : ValueTask.FromResult(result);
    }
}
