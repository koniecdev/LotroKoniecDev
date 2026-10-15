namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// The outbox payload for "someone tried to register with this account's address". The producer
/// serializes it, and the relay and the consumer read it back.
/// </summary>
/// <remarks>
/// Registration answers the same for a free and a taken address (#860), so the owner of a taken one
/// learns about the attempt from this notice instead. It carries the id only, like
/// <see cref="EmailConfirmationRequested"/>: the consumer reads the address off the account when it sends,
/// so a notice that waited in the queue never goes to an address the account has left.
/// </remarks>
public sealed record RegistrationAttemptedOnExistingAccount(Guid IdentityUserId);
