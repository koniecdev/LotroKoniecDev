using LotroKoniecDev.SharedKernel.Guards;

namespace LotroKoniecDev.AuthSystem.Persistence.Sessions;

/// <summary>
/// One sign-in to the sign-in server: the authentication ticket behind its cookie, which carries only
/// <see cref="Id"/> (ADR-0062).
/// </summary>
public sealed class SignInSession
{
    public Guid Id { get; }
    public Guid UserId { get; }

    /// <summary>
    /// The serialized ticket, protected with Data Protection. It holds the user's id, name, e-mail and
    /// roles, so it is never stored in the clear.
    /// </summary>
    public byte[] ProtectedTicket { get; }

    public DateTimeOffset ExpiresAt { get; }

    public static SignInSession Create(Guid userId, byte[] protectedTicket, DateTimeOffset expiresAt)
    {
        Ensure.NotEmpty(userId);
        ArgumentNullException.ThrowIfNull(protectedTicket);
        Ensure.NotEmpty(expiresAt);

        // Random and not time-ordered: the id is the session key, and nothing reads these rows in order.
        Guid id = Guid.NewGuid();
        SignInSession instance = new(id: id, userId: userId, protectedTicket: protectedTicket, expiresAt: expiresAt);
        return instance;
    }

    private SignInSession(Guid id, Guid userId, byte[] protectedTicket, DateTimeOffset expiresAt)
    {
        Id = id;
        UserId = userId;
        ProtectedTicket = protectedTicket;
        ExpiresAt = expiresAt;
    }

    private SignInSession()
    {
        ProtectedTicket = [];
    }
}
