using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Auth.Provisioning;

/// <summary>
/// Gets or creates the caller's <c>Translator</c> from the current claims (ADR-0004). The middleware
/// calls it on every authenticated request, and every write that stamps a <see cref="TranslatorId"/>
/// calls it again first. Repeat calls only write when the claims changed.
/// </summary>
internal interface ITranslatorProvisioner
{
    ValueTask<Result<TranslatorId>> ProvisionCurrentAsync(CancellationToken cancellationToken);
}
