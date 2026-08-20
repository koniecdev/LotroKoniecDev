using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Infrastructure.Hateoas;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Mvc;
using TranslationDiscoveryResponse = LotroKoniecDev.TranslationSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Infrastructure.Discovery;

/// <summary>
/// The one place a page gets its first TMS URL (#610, ADR-0041): read the service document, then look up
/// the entry point by rel name. There is no API gateway and no shared path constant. The discovery
/// document is the contract, so a rel the server does not send is something this caller may not do, and
/// the answer is a failure and not a path built here. It works like <c>AccountLoader</c> on the auth
/// side.
/// </summary>
internal static class TranslationSystemEntryPointExtensions
{
    extension(IDiscoveryCache discoveryCache)
    {
        /// <summary>
        /// The href the TMS sends for <paramref name="rel"/>, or a failure. That failure carries either
        /// discovery's own <see cref="ProblemDetails"/>, when the API is unreachable, or a 403, when the
        /// caller is not offered this action. It never falls back to a guessed path.
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
    /// The failure for a rel the service document does not offer this caller. It is a 403, because the
    /// action exists but this session may not use it. The rel name goes into <c>Detail</c>, so a support
    /// report names the missing entry point instead of saying "coś poszło nie tak".
    /// </summary>
    private static ProblemDetails MissingEntryPoint(string rel) => ApiProblemCopy.FrontendAuthored(
        "Ta funkcja jest niedostępna",
        $"Serwer nie udostępnia tej sesji zasobu „{rel}”. Zaloguj się ponownie lub spróbuj później.",
        StatusCodes.Status403Forbidden);
}
