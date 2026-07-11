namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

/// <summary>
/// Classifies a failed <see cref="ApiResult"/> by its authentication/authorization status so call
/// sites can react uniformly: a <c>401</c> means the cookie was accepted locally but the bearer token
/// was rejected upstream (an expired session) and the user should be sent back through the login flow,
/// while a <c>403</c> means the user is signed in but lacks the role/ownership for the action and only
/// needs a clear "not permitted" message — never a redirect.
/// </summary>
internal static class ApiResultAuthExtensions
{
    extension(ApiResult result)
    {
        public bool IsUnauthorized =>
            result.IsFailure && result.ProblemDetails?.Status == StatusCodes.Status401Unauthorized;

        public bool IsForbidden =>
            result.IsFailure && result.ProblemDetails?.Status == StatusCodes.Status403Forbidden;
    }
}
