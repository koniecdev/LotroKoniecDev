using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Core.BuildingBlocks;

namespace LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslatorAggregate;

/// <summary>
/// The translator profile the editor and the list read for the submitter's and approver's display
/// name (ADR-0004). It maps the same <c>Translators</c> table as the write aggregate.
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
