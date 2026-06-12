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
        await db.Database.ExecuteSqlRawAsync("""
                                             TRUNCATE TABLE
                                                 auth."UserRoles",
                                                 auth."UserClaims",
                                                 auth."UserLogins",
                                                 auth."UserTokens",
                                                 auth."OpenIddictTokens",
                                                 auth."OpenIddictAuthorizations",
                                                 auth."Users"
                                             RESTART IDENTITY CASCADE;
                                             """);
    }
}
