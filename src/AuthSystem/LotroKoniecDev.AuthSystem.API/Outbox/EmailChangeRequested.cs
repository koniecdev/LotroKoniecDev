namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// The outbox payload for "someone asked to move this account to another address". The producer
/// serializes it, and the relay and the consumer read it back.
/// </summary>
/// <remarks>
/// It carries both addresses, unlike <see cref="EmailConfirmationRequested"/>, which carries only the
/// id. Reading the current address off the user row at send time would be wrong here: after a delayed
/// or dead-lettered redelivery the row already holds the new address, and the warning meant for the
/// old mailbox would go to the very address the attacker chose. <c>CurrentEmail</c> also tells the
/// processor whether the request is still open at all.
/// The confirmation token still belongs to the sending step, so the processor creates it, and the
/// "the link expires in 24 hours" sentence stays true no matter how long the message waited.
/// </remarks>
public sealed record EmailChangeRequested(Guid IdentityUserId, string CurrentEmail, string NewEmail);
