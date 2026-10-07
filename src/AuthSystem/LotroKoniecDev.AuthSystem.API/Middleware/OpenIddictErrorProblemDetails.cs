using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// The status-code pages know only the status of a refused sign-in or sign-out link (#912). This copies
/// the OAuth error into the problem details, so a program still learns why the link was refused.
/// </summary>
/// <remarks>
/// Only authorize and logout. Userinfo gives its error in the <c>WWW-Authenticate</c> header and leaves
/// the body to the status-code pages too, but the bearer rules (RFC 6750 §3.1) say a request with no
/// token gets no error code, so its body must not name one either.
/// <para>
/// The <c>state</c> parameter is left out on purpose, as OpenIddict's own plain-text answer leaves it out.
/// </para>
/// </remarks>
internal static class OpenIddictErrorProblemDetails
{
    internal static void Add(ProblemDetailsContext context)
    {
        OpenIddictServerTransaction? transaction =
            context.HttpContext.Features.Get<OpenIddictServerAspNetCoreFeature>()?.Transaction;
        if (transaction is not
            {
                EndpointType: OpenIddictServerEndpointType.Authorization or OpenIddictServerEndpointType.EndSession,
                Response: { Error: { Length: > 0 } error } response
            })
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
