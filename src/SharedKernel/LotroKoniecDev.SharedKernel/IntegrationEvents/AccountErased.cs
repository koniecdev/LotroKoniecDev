namespace LotroKoniecDev.SharedKernel.IntegrationEvents;

/// <summary>
/// "This account has been erased for good." The AuthSystem writes it to its outbox in the same save
/// that anonymizes the account, and the TranslationSystem erases its own copy of the person's name
/// and address when it arrives (ADR-0065).
/// </summary>
/// <remarks>
/// It is the one message the two contexts share, so both read this record and not a copy of it.
/// The AMQP <c>type</c> on the wire is the record's name, so renaming it breaks the consumer that
/// is already running.
/// </remarks>
public sealed record AccountErased(Guid IdentityUserId);
