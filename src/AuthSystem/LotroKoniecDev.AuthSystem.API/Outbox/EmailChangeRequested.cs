namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// "Someone asked to move this account to another address."
/// </summary>
/// <remarks>
/// Both addresses, unlike the id-only payloads: after a late redelivery the row may already hold the new
/// address, and the warning would go to the address the attacker chose. <c>CurrentEmail</c> also tells
/// the processor whether the request is still open. The token is still created at send time, so "the
/// link expires in 24 hours" stays true.
/// </remarks>
public sealed record EmailChangeRequested(Guid IdentityUserId, string CurrentEmail, string NewEmail);
