namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

/// <summary>
/// A <c>401</c> means the API refused the token, so the user must sign in again. A <c>403</c> means the
/// user may not do this and gets a "not allowed" message, never a redirect.
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
