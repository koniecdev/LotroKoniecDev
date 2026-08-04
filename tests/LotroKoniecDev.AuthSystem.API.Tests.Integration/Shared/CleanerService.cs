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

        // PostgreSQL preserves the casing of identifiers EF Core emits, so we have to quote them.
        // TRUNCATE ... CASCADE is faster than DELETE and handles FK chains in one statement.
        // Outbox and inbox carry no FK to Users, so they must be listed explicitly — otherwise an
        // unprocessed row left by one test (e.g. an unroutable type) keeps failing every relay
        // pass of every later test in the collection.
        await db.Database.ExecuteSqlRawAsync("""
                                             TRUNCATE TABLE
                                                 authsystem."UserRoles",
                                                 authsystem."UserClaims",
                                                 authsystem."UserLogins",
                                                 authsystem."UserTokens",
                                                 authsystem."OpenIddictTokens",
                                                 authsystem."OpenIddictAuthorizations",
                                                 authsystem."Users",
                                                 authsystem."OutboxMessages",
                                                 authsystem."InboxMessages"
                                             RESTART IDENTITY CASCADE;
                                             """);
    }
}
