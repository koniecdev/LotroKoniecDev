namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// The outbox payload for "tell this user their deletion was cancelled". The producer serializes it,
/// and the relay and the consumer read it back.
/// </summary>
/// <remarks>
/// It carries the user id and nothing else (ADR-0038 decision 2), and no token on purpose. The e-mail
/// only informs and points at the password reset form. The reset token itself is in the cancel
/// endpoint's response, created by the handler after its commit.
/// The table in epic #578 claimed this e-mail carried more than it does. The list in the ADR, which
/// was checked against the code, is the correct one.
/// </remarks>
public sealed record AccountDeletionCancelled(Guid IdentityUserId);
