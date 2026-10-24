namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// "Someone tried to register with this account's address" (#860).
/// </summary>
/// <remarks>
/// It carries the typed address, so the consumer can drop the notice when the account moved to another
/// address while the message waited. An erasure cuts the payload back to the id (ADR-0065).
/// </remarks>
public sealed record RegistrationAttemptedOnExistingAccount(Guid IdentityUserId, string AttemptedEmail);
