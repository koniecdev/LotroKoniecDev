using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Auth.Provisioning;

/// <summary>
/// Lazily and idempotently provisions the TMS-local <c>Translator</c> for the authenticated caller
/// (ADR-0004, pattern KittySaver ADR-0007 §4): returns the existing <see cref="TranslatorId"/> if a
/// row already exists for the caller's identity, otherwise creates one from the current claims. Call
/// it as the first step of any write that stamps a <see cref="TranslatorId"/>; repeat calls add no
/// duplicate rows.
/// </summary>
internal interface ITranslatorProvisioner
{
    ValueTask<Result<TranslatorId>> ProvisionCurrentAsync(CancellationToken cancellationToken);
}
