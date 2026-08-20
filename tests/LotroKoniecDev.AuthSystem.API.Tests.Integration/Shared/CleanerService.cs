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

        // PostgreSQL keeps the case of the identifiers EF Core writes, so we have to quote them.
        // TRUNCATE ... CASCADE is faster than DELETE and clears whole foreign-key chains in one
        // statement.
        // The outbox and inbox tables have no foreign key to Users, so they must be listed here. Without
        // that, an unprocessed row left behind by one test, for example one with an unroutable type,
        // keeps failing every relay pass in every later test of the collection.
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
