using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// Reads the stored state of a reference token or of a user's authorizations. A refused refresh does not
/// prove that a revoke ran, because the security stamp check refuses it too (#848), so revocation and
/// lifetime tests (#1014) read the row.
/// </summary>
internal static class OpenIddictTokenState
{
    public static async Task<string?> StatusOfAsync(IServiceProvider services, string referenceToken)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IOpenIddictTokenManager tokenManager = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();

        object? token = await tokenManager.FindByReferenceIdAsync(referenceToken);
        return token is null ? null : await tokenManager.GetStatusAsync(token);
    }

    public static async Task<(DateTimeOffset? CreatedAt, DateTimeOffset? ExpiresAt)> DatesOfAsync(
        IServiceProvider services,
        string referenceToken)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IOpenIddictTokenManager tokenManager = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();

        object? token = await tokenManager.FindByReferenceIdAsync(referenceToken);
        if (token is null)
        {
            return (null, null);
        }

        return (await tokenManager.GetCreationDateAsync(token), await tokenManager.GetExpirationDateAsync(token));
    }

    public static async Task<List<string?>> AuthorizationStatusesOfAsync(IServiceProvider services, string subject)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IOpenIddictAuthorizationManager authorizationManager =
            scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();

        List<string?> statuses = [];
        await foreach (object authorization in authorizationManager.FindBySubjectAsync(subject))
        {
            statuses.Add(await authorizationManager.GetStatusAsync(authorization));
        }

        return statuses;
    }
}
