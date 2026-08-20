using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Auth.Provisioning;

/// <summary>
/// Creates the TMS-side <c>Translator</c> for the authenticated caller when it is first needed, and it
/// is safe to call twice (ADR-0004, the pattern from KittySaver ADR-0007 §4). It returns the existing
/// <see cref="TranslatorId"/> when a row for the caller's identity is already there, and otherwise
/// creates one from the current claims.
/// The provisioning middleware calls it on the caller's first authenticated request (ADR-0004, amended
/// 2026-06-24), so a user who registered and logged in has a profile before any write. Every write
/// that stamps a <see cref="TranslatorId"/> calls it again as its first step. Repeat calls add no
/// duplicate rows and only write when the claims changed.
/// </summary>
internal interface ITranslatorProvisioner
{
    ValueTask<Result<TranslatorId>> ProvisionCurrentAsync(CancellationToken cancellationToken);
}
