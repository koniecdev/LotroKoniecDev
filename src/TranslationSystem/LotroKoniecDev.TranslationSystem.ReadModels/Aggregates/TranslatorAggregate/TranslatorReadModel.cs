using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Core.BuildingBlocks;

namespace LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslatorAggregate;

/// <summary>
/// The read side of the TMS translator profile (ADR-0004): the display name the editor and the list
/// show for the submitter and the approver, keyed by the <see cref="IdentityId"/> that the AuthSystem
/// owns. It maps onto the same <c>Translators</c> table as the write aggregate.
/// </summary>
public sealed record TranslatorReadModel(
    TranslatorId Id,
    IdentityId IdentityId,
    string DisplayName,
    string? Email,
    DateTimeOffset ProvisionedAt) : IReadOnlyEntity<TranslatorId>
{
    DateTimeOffset IReadOnlyEntity<TranslatorId>.CreatedAt => ProvisionedAt;
}
