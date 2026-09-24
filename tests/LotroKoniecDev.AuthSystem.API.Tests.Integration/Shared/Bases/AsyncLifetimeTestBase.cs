using Bogus;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;

public abstract class AsyncLifetimeTestBase : IAsyncLifetime
{
    protected Faker Faker { get; } = new();
    protected abstract TestApiClient ApiClient { get; }

    protected AuthSystemApiFactory Factory { get; }
    protected SpyAccountConfirmationEmailSender AccountConfirmationEmailSpy { get; }
    protected SpyPasswordResetEmailSender PasswordResetEmailSpy { get; }
    protected SpyAccountDeletionEmailSender AccountDeletionEmailSpy { get; }
    protected SpyEmailChangeEmailSender EmailChangeEmailSpy { get; }

    protected AsyncLifetimeTestBase(AuthSystemApiFactory factory)
    {
        Factory = factory;
        AccountConfirmationEmailSpy = factory.Services.GetRequiredService<SpyAccountConfirmationEmailSender>();
        PasswordResetEmailSpy = factory.Services.GetRequiredService<SpyPasswordResetEmailSender>();
        AccountDeletionEmailSpy = factory.Services.GetRequiredService<SpyAccountDeletionEmailSender>();
        EmailChangeEmailSpy = factory.Services.GetRequiredService<SpyEmailChangeEmailSender>();
    }

    public virtual async Task InitializeAsync()
    {
        AccountDeletionEmailSpy.Reset();
        EmailChangeEmailSpy.Reset();
        DisarmDatabaseFailures();

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        CleanerService cleaner = scope.ServiceProvider.GetRequiredService<CleanerService>();
        await cleaner.CleanAsync();
    }

    /// <summary>
    /// Disarms on the way out as well: a test that fails before its armed failure fires must not hand
    /// it to the next test, and not every class in the collection derives from this base.
    /// </summary>
    public virtual Task DisposeAsync()
    {
        DisarmDatabaseFailures();
        return Task.CompletedTask;
    }

    private void DisarmDatabaseFailures()
    {
        Factory.DbCommandFailures.Disarm();
        Factory.DbCommitFailures.Disarm();
    }
}
