using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

internal sealed class CleanerService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public CleanerService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task CleanAsync()
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        // PostgreSQL keeps the case of the identifiers EF Core writes, so they are quoted. TRUNCATE ...
        // CASCADE clears whole foreign-key chains in one statement. The outbox and inbox tables have no
        // foreign key to Users, so they are listed: an unprocessed row left by one test, such as one with
        // an unroutable type, would fail every relay pass in every later test.
        await db.Database.ExecuteSqlRawAsync("""
                                             TRUNCATE TABLE
                                                 authsystem."UserRoles",
                                                 authsystem."UserClaims",
                                                 authsystem."UserLogins",
                                                 authsystem."UserTokens",
                                                 authsystem."OpenIddictTokens",
                                                 authsystem."OpenIddictAuthorizations",
                                                 authsystem."SignInSessions",
                                                 authsystem."Users",
                                                 authsystem."OutboxMessages",
                                                 authsystem."InboxMessages"
                                             RESTART IDENTITY CASCADE;
                                             """);
    }
}
