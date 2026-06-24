using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Auth.Provisioning;

/// <summary>
/// Lazily and idempotently provisions the TMS-local <c>Translator</c> for the authenticated caller
/// (ADR-0004, pattern KittySaver ADR-0007 §4): returns the existing <see cref="TranslatorId"/> if a
/// row already exists for the caller's identity, otherwise creates one from the current claims.
/// Invoked eagerly on the caller's first authenticated request (the provisioning middleware, ADR-0004
/// amendment 2026-06-24) so a registered + logged-in user has a profile before any write, and again
/// as the authoritative first step of any write that stamps a <see cref="TranslatorId"/>. Idempotent
/// — repeat calls add no duplicate rows and only write when the claims changed.
/// </summary>
internal interface ITranslatorProvisioner
{
    ValueTask<Result<TranslatorId>> ProvisionCurrentAsync(CancellationToken cancellationToken);
}
