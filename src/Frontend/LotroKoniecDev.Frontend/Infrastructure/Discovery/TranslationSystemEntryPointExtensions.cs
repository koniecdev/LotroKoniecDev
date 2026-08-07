using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Infrastructure.Hateoas;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Mvc;
using TranslationDiscoveryResponse = LotroKoniecDev.TranslationSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Infrastructure.Discovery;

/// <summary>
/// The single seam through which a page gets its <em>first</em> TMS URL (#610 / ADR-0041): resolve the
/// service document, then read the entry point by rel name. There is no API gateway and no shared path
/// constant — the discovery document is the contract surface, so a rel the server does not advertise is
/// an affordance this caller does not have, and the answer is a failure rather than a locally composed
/// path. Mirrors <c>AccountLoader</c>'s auth-side resolution.
/// </summary>
internal static class TranslationSystemEntryPointExtensions
{
    extension(IDiscoveryCache discoveryCache)
    {
        /// <summary>
        /// The href the TMS advertises for <paramref name="rel"/>, or a failure carrying the discovery
        /// outage's own <see cref="ProblemDetails"/> (unreachable API) or a 403 (the caller is not
        /// offered this affordance). Never falls back to a guessed path.
        /// </summary>
        public async Task<ApiResult<string>> ResolveTranslationSystemHrefAsync(
            string rel,
            CancellationToken cancellationToken = default)
        {
            ApiResult<TranslationDiscoveryResponse> discoveryResult =
                await discoveryCache.GetTranslationSystemDiscoveryAsync(cancellationToken);
            if (discoveryResult.IsFailure)
            {
                return ApiResult.Failure<string>(discoveryResult.ProblemDetails!);
            }

            LinkDto? link = discoveryResult.Value.Links.FindLink(rel);

            return link is null
                ? ApiResult.Failure<string>(MissingEntryPoint(rel))
                : ApiResult.Success(link.Href);
        }
    }

    /// <summary>
    /// The failure for a rel the service document does not offer this caller. A 403 because the
    /// affordance exists but is not this session's to use — the rel travels in <c>Detail</c> so a
    /// support report names the missing entry point instead of "coś poszło nie tak".
    /// </summary>
    private static ProblemDetails MissingEntryPoint(string rel) => ApiProblemCopy.FrontendAuthored(
        "Ta funkcja jest niedostępna",
        $"Serwer nie udostępnia tej sesji zasobu „{rel}”. Zaloguj się ponownie lub spróbuj później.",
        StatusCodes.Status403Forbidden);
}
