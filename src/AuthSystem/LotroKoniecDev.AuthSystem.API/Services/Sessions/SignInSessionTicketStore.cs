using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Sessions;

namespace LotroKoniecDev.AuthSystem.API.Services.Sessions;

/// <summary>
/// The session store of the sign-in server's cookie (ADR-0062, #1013). The ticket lives in the auth
/// database and the cookie carries only its key, so signing out deletes the session on the server and
/// every copy of the cookie stops working with it.
/// </summary>
/// <remarks>
/// A singleton, because the cookie options hold one instance. Each call opens its own scope, so the
/// store never saves changes that belong to the request's own <see cref="AuthDbContext"/>.
/// </remarks>
internal sealed class SignInSessionTicketStore : ITicketStore
{
    /// <summary>
    /// Not the cookie's own purpose, so a stored ticket can never be pasted in as a cookie.
    /// </summary>
    private const string ProtectorPurpose = "LotroKoniecDev.AuthSystem.SignInSessions.v1";

    private const string KeyFormat = "N";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IDataProtector _protector;

    public SignInSessionTicketStore(
        IServiceScopeFactory serviceScopeFactory,
        IDataProtectionProvider dataProtectionProvider)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
    }

    public Task<string> StoreAsync(AuthenticationTicket ticket) =>
        StoreAsync(ticket, CancellationToken.None);

    public async Task<string> StoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        SignInSession session = SignInSession.Create(UserIdOf(ticket), Protect(ticket), ExpiryOf(ticket));

        await using AsyncServiceScope scope = _serviceScopeFactory.CreateAsyncScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        dbContext.SignInSessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);

        return session.Id.ToString(KeyFormat);
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket) =>
        RenewAsync(key, ticket, CancellationToken.None);

    /// <summary>
    /// An update and never an insert: a session that a sign-out in another tab deleted stays deleted.
    /// </summary>
    public async Task RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        if (!TryParseKey(key, out Guid id))
        {
            return;
        }

        byte[] protectedTicket = Protect(ticket);
        DateTimeOffset expiresAt = ExpiryOf(ticket);

        await using AsyncServiceScope scope = _serviceScopeFactory.CreateAsyncScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await dbContext.SignInSessions
            .Where(session => session.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(session => session.ProtectedTicket, protectedTicket)
                    .SetProperty(session => session.ExpiresAt, expiresAt),
                cancellationToken);
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key) =>
        RetrieveAsync(key, CancellationToken.None);

    public async Task<AuthenticationTicket?> RetrieveAsync(string key, CancellationToken cancellationToken)
    {
        if (!TryParseKey(key, out Guid id))
        {
            return null;
        }

        await using AsyncServiceScope scope = _serviceScopeFactory.CreateAsyncScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        byte[]? protectedTicket = await dbContext.SignInSessions
            .Where(session => session.Id == id)
            .Select(session => session.ProtectedTicket)
            .SingleOrDefaultAsync(cancellationToken);

        return protectedTicket is null ? null : Unprotect(protectedTicket);
    }

    public Task RemoveAsync(string key) =>
        RemoveAsync(key, CancellationToken.None);

    /// <summary>
    /// Ignores the request's token on purpose: a sign-out whose browser leaves before the answer must
    /// still end the session.
    /// </summary>
    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        if (!TryParseKey(key, out Guid id))
        {
            return;
        }

        await using AsyncServiceScope scope = _serviceScopeFactory.CreateAsyncScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await dbContext.SignInSessions
            .Where(session => session.Id == id)
            .ExecuteDeleteAsync(CancellationToken.None);
    }

    private byte[] Protect(AuthenticationTicket ticket) =>
        _protector.Protect(TicketSerializer.Default.Serialize(ticket));

    /// <summary>
    /// A row that no key can open any more reads as no session, the same answer the cookie handler gives
    /// a cookie it cannot open: the user signs in again.
    /// </summary>
    private AuthenticationTicket? Unprotect(byte[] protectedTicket)
    {
        byte[] serializedTicket;
        try
        {
            serializedTicket = _protector.Unprotect(protectedTicket);
        }
        catch (CryptographicException)
        {
            return null;
        }

        return TicketSerializer.Default.Deserialize(serializedTicket);
    }

    private static Guid UserIdOf(AuthenticationTicket ticket) =>
        Guid.TryParse(ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier), out Guid userId)
            ? userId
            : throw new InvalidOperationException("A sign-in to the sign-in server must name its user.");

    /// <summary>
    /// The cookie handler always sets the expiry before it stores or renews a ticket.
    /// </summary>
    private static DateTimeOffset ExpiryOf(AuthenticationTicket ticket) =>
        ticket.Properties.ExpiresUtc
        ?? throw new InvalidOperationException("The cookie handler stored a ticket with no expiry.");

    private static bool TryParseKey(string key, out Guid id) =>
        Guid.TryParseExact(key, KeyFormat, out id);
}
