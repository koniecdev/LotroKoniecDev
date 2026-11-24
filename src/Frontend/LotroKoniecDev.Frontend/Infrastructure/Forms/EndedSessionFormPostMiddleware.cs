using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Diagnostics;

namespace LotroKoniecDev.Frontend.Infrastructure.Forms;

/// <summary>
/// A page anyone may open, such as the translation list, can hold forms for a signed-in user (#1493). When
/// the session ends before such a form is sent (it expired, or the user signed out in another tab), the
/// form's antiforgery token no longer fits, and Blazor would answer with a bare 400. This sends the caller
/// to sign in and back to the address the form was sent to, as a page that needs a sign-in does. It covers
/// a post with no session to any Razor page open to anyone, a post that carries no token too: the form
/// cannot be read once its token failed the check.
/// </summary>
internal sealed class EndedSessionFormPostMiddleware
{
    private readonly RequestDelegate _next;

    public Task InvokeAsync(HttpContext context)
    {
        // The default challenge is the cookie scheme's: a redirect to the sign-in path with this address.
        return IsFormPostFromAnEndedSession(context)
            ? context.ChallengeAsync()
            : _next(context);
    }

    public EndedSessionFormPostMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>
    /// Never on the second run that draws an error page for another answer: that address is not where the
    /// form was sent, so the sign-in would lead back to the error page.
    /// </summary>
    private static bool IsFormPostFromAnEndedSession(HttpContext context) =>
        HttpMethods.IsPost(context.Request.Method)
        && context.User.Identity?.IsAuthenticated != true
        && context.Features.Get<IStatusCodeReExecuteFeature>() is null
        && context.Features.Get<IExceptionHandlerFeature>() is null
        && context.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false }
        && context.GetEndpoint()?.Metadata.GetMetadata<ComponentTypeMetadata>() is not null;
}
