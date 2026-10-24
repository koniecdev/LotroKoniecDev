namespace LotroKoniecDev.SharedKernel.IntegrationEvents;

/// <summary>
/// The AuthSystem writes it to its outbox in the same save that anonymizes the account, and the
/// TranslationSystem then erases its own copy of the person's name and address (ADR-0065).
/// </summary>
/// <remarks>
/// Both contexts read this record, never a copy of it. The AMQP <c>type</c> on the wire is the
/// record's name, so renaming it breaks the consumer that is already running.
/// </remarks>
public sealed record AccountErased(Guid IdentityUserId);
