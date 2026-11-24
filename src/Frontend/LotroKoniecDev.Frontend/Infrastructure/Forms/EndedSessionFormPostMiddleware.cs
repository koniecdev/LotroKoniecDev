using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Endpoints;

namespace LotroKoniecDev.Frontend.Infrastructure.Forms;

/// <summary>
/// A page anyone may open, such as the translation list, can hold forms for a signed-in user (#1493). When
/// the session ends before such a form is sent (it expired, or the user signed out in another tab), the
/// form's antiforgery token no longer fits, and Blazor would answer with a bare 400. This sends the caller
/// to sign in and back to the same address instead, as a page that needs a sign-in does.
/// </summary>
internal sealed class EndedSessionFormPostMiddleware
{
    private readonly RequestDelegate _next;

    public EndedSessionFormPostMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        bool postFromAnEndedSession = HttpMethods.IsPost(context.Request.Method)
            && context.User.Identity?.IsAuthenticated != true
            && context.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false }
            && context.GetEndpoint()?.Metadata.GetMetadata<ComponentTypeMetadata>() is not null;

        // The default challenge is the cookie scheme's: a redirect to the sign-in path with this address.
        return postFromAnEndedSession
            ? context.ChallengeAsync()
            : _next(context);
    }
}
