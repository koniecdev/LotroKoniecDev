using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Core.BuildingBlocks;

namespace LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslatorAggregate;

/// <summary>
/// Read projection of the TMS-local translator identity (ADR-0004): the display name the editor and
/// list render for submitter/approver, keyed by the cross-context <see cref="IdentityId"/>. Maps
/// onto the same <c>Translators</c> table the write aggregate owns.
/// </summary>
public sealed record TranslatorReadModel(
    TranslatorId Id,
    IdentityId IdentityId,
    string DisplayName,
    string? Email,
    DateTimeOffset ProvisionedAt,
    DateTimeOffset LastSeenAt) : IReadOnlyEntity<TranslatorId>
{
    DateTimeOffset IReadOnlyEntity<TranslatorId>.CreatedAt => ProvisionedAt;
}
