namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

/// <summary>
/// Sorts a failed <see cref="ApiResult"/> by what its status says about login and permissions, so every
/// call site reacts the same way.
/// A <c>401</c> means the cookie was accepted here but the token was refused by the API, so the session
/// has expired and the user should go through login again.
/// A <c>403</c> means the user is logged in but may not do this, and only needs a clear "not allowed"
/// message, never a redirect.
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
