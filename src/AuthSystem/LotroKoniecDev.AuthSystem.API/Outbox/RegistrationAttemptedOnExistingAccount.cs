namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// The outbox payload for "someone tried to register with this account's address". The producer
/// serializes it, and the relay and the consumer read it back.
/// </summary>
/// <remarks>
/// Registration answers the same for a free and a taken address (#860), so the owner of a taken one
/// learns about the attempt from this notice instead. It carries the typed address next to the id, like
/// <see cref="EmailChangeRequested"/> carries the current one: if the account moved to another address
/// while the message waited, the notice would tell the new address about an attempt on the old one, so
/// the consumer drops it. An erasure cuts the payload back to the id (ADR-0065).
/// </remarks>
public sealed record RegistrationAttemptedOnExistingAccount(Guid IdentityUserId, string AttemptedEmail);
