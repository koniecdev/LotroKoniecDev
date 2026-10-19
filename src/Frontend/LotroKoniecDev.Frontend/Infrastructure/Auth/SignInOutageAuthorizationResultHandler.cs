using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http.Extensions;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth;

/// <summary>
/// A visitor who is not signed in and opens a page or route that needs a login is sent to the sign-in
/// server by the authorization middleware, and that redirect needs the sign-in server's discovery document.
/// While the sign-in server is down it cannot be built, and the visitor got the error page (#1153). So the
/// visitor is sent to the login route instead, which shows the "login temporarily unavailable" page (#311).
/// A reload there tries the sign-in again and comes back to the same page, like the sign-in redirect does.
/// </summary>
/// <remarks>
/// A redirect and not a 503 on the same address, for two reasons. The status-code page runs again with the
/// request's own method, and Blazor answers a POST there with an empty 400 page (#1122). And after a session
/// has just ended (#1058) that second run would read the deleted cookie again and show the user as signed in.
/// The redirect answer still carries the header that deletes the cookie.
/// The login route checks the sign-in server again, so a sign-in server that times out instead of refusing
/// makes the visitor wait twice. A sign-in server that is just down is refused at once by the proxy.
/// When the sign-in server answers, and for every other outcome, the framework's own handler runs unchanged.
/// </remarks>
internal sealed class SignInOutageAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    // A fixed local start with the "?" in it, so the visitor's address can only ever land in the query.
    private const string LoginWithReturnUrl = AuthenticationDependencyInjectionExtensions.LoginPath + "?returnUrl=";

    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();
    private readonly SignInAvailability _signInAvailability;

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Challenged
            && !await _signInAvailability.CanRedirectToSignInAsync(context.RequestAborted))
        {
            // The same address the sign-in redirect would come back to.
            string returnUrl = UriHelper.BuildRelative(
                context.Request.PathBase,
                context.Request.Path,
                context.Request.QueryString);
            context.Response.Redirect(LoginWithReturnUrl + Uri.EscapeDataString(returnUrl));
            return;
        }

        await _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }

    public SignInOutageAuthorizationResultHandler(SignInAvailability signInAvailability)
    {
        _signInAvailability = signInAvailability;
    }
}
