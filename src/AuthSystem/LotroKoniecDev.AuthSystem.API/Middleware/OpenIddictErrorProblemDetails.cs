using Microsoft.AspNetCore;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// OpenIddict leaves a refused sign-in or sign-out link to the status-code pages (#912), and they know
/// only the status. This copies the OAuth error into the problem details, so a program still learns why
/// the link was refused. A browser never sees it: <see cref="BrowserErrorPageWriter"/> shows the general
/// page instead.
/// </summary>
/// <remarks>
/// The <c>state</c> parameter is left out on purpose, as OpenIddict's own plain-text answer leaves it out.
/// </remarks>
internal static class OpenIddictErrorProblemDetails
{
    internal static void Add(ProblemDetailsContext context)
    {
        OpenIddictResponse? response = context.HttpContext.GetOpenIddictServerResponse();
        if (response?.Error is not { Length: > 0 } error)
        {
            return;
        }

        IDictionary<string, object?> extensions = context.ProblemDetails.Extensions;
        extensions[Parameters.Error] = error;

        if (response.ErrorDescription is { Length: > 0 } errorDescription)
        {
            extensions[Parameters.ErrorDescription] = errorDescription;
        }

        if (response.ErrorUri is { Length: > 0 } errorUri)
        {
            extensions[Parameters.ErrorUri] = errorUri;
        }
    }
}
