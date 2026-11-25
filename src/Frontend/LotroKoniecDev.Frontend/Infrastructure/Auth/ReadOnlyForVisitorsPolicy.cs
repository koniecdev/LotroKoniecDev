using Microsoft.AspNetCore.Authorization;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth;

/// <summary>
/// For a page anyone may read that holds forms for a signed-in user, such as the translation list (#309).
/// Only a signed-in user may post to it. The sign-in check then runs before the form's token check, so a
/// form sent after the session ended goes to sign-in and back to the same address, not to a blank 400
/// (#1493).
/// </summary>
internal static class ReadOnlyForVisitorsPolicy
{
    public const string Name = "ReadOnlyForVisitors";

    public static AuthorizationPolicy Policy { get; } = new AuthorizationPolicyBuilder()
        .RequireAssertion(context => context.User.Identity is { IsAuthenticated: true } || IsARead(context.Resource))
        .Build();

    /// <summary>
    /// The page's own check while it draws passes no request, and the request was already checked by then.
    /// So never use this policy to decide what to draw: it lets through anything that is not a request.
    /// </summary>
    private static bool IsARead(object? resource) =>
        resource is not HttpContext httpContext || HttpMethods.IsGet(httpContext.Request.Method);
}
