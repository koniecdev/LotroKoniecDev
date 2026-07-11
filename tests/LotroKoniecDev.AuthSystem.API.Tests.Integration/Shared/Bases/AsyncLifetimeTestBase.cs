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

    protected AsyncLifetimeTestBase(AuthSystemApiFactory factory)
    {
        Factory = factory;
        AccountConfirmationEmailSpy = factory.Services.GetRequiredService<SpyAccountConfirmationEmailSender>();
        PasswordResetEmailSpy = factory.Services.GetRequiredService<SpyPasswordResetEmailSender>();
        AccountDeletionEmailSpy = factory.Services.GetRequiredService<SpyAccountDeletionEmailSender>();
    }

    public virtual async Task InitializeAsync()
    {
        AccountDeletionEmailSpy.Reset();

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        CleanerService cleaner = scope.ServiceProvider.GetRequiredService<CleanerService>();
        await cleaner.CleanAsync();
    }

    public virtual Task DisposeAsync() => Task.CompletedTask;
}
