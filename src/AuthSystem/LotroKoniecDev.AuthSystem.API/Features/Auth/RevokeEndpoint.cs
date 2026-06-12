using Microsoft.AspNetCore;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.Common;


namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

internal sealed partial class RevokeEndpoint : IEndpoint
{
    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        IOpenIddictTokenManager tokenManager,
        ILogger<RevokeEndpoint> logger)
    {
        OpenIddictRequest request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        if (string.IsNullOrEmpty(request.Token))
        {
            return Results.Ok();
        }

        object? token = await tokenManager.FindByReferenceIdAsync(request.Token);

        if (token is not null)
        {
            await tokenManager.TryRevokeAsync(token);
            string? tokenId = await tokenManager.GetIdAsync(token);
            LogTokenRevoked(logger, tokenId);
        }

        return Results.Ok();
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("connect/revoke", HandleAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();
    }

    [LoggerMessage(EventId = EventIds.TokenRevoked, Level = LogLevel.Information, Message = "Token revoked. TokenId: {TokenId}")]
    private static partial void LogTokenRevoked(ILogger logger, string? tokenId);
}
