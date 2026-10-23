using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Infrastructure.Hateoas;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Mvc;
using TranslationDiscoveryResponse = LotroKoniecDev.TranslationSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Infrastructure.Discovery;

/// <summary>
/// The one place a page gets its first TMS URL: by rel name from the service document (ADR-0041). A rel
/// the server does not send is something this caller may not do, so the answer is a failure, never a path
/// built here (#610).
/// </summary>
internal static class TranslationSystemEntryPointExtensions
{
    extension(IDiscoveryCache discoveryCache)
    {
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
    /// A 403: the action exists but this session may not use it. The rel name goes into <c>Detail</c>, so
    /// a support report names the missing entry point.
    /// </summary>
    private static ProblemDetails MissingEntryPoint(string rel) => ApiProblemCopy.FrontendAuthored(
        "Ta funkcja jest niedostępna",
        $"Serwer nie udostępnia tej sesji zasobu „{rel}”. Zaloguj się ponownie lub spróbuj później.",
        StatusCodes.Status403Forbidden);
}
