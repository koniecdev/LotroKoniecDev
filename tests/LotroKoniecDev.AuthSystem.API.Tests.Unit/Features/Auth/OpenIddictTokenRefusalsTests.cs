using LotroKoniecDev.AuthSystem.API.Features.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Features.Auth;

/// <summary>
/// The warning for a refusal OpenIddict makes itself only writes to the log (#977). When it cannot read the
/// stored token to name the user, OpenIddict's refusal must still go out as a refusal, not as a server error.
/// </summary>
public sealed class OpenIddictTokenRefusalsTests
{
    private readonly IOpenIddictTokenManager _tokenManager = Substitute.For<IOpenIddictTokenManager>();

    [Theory]
    [InlineData(GrantTypes.RefreshToken)]
    [InlineData(GrantTypes.AuthorizationCode)]
    public async Task WarnWhenRefused_WhenTheStoredTokenCannotBeRead_ShouldNotThrow(string grantType)
    {
        // Arrange
        _tokenManager.FindByReferenceIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("The database is unreachable."));
        OpenIddictServerEvents.ProcessErrorContext context = RefusedTokenRequest(new OpenIddictRequest
        {
            GrantType = grantType,
            Code = "stored-code-id",
            RefreshToken = "stored-refresh-token-id"
        });
        OpenIddictTokenRefusals.WarnWhenRefused handler = new(_tokenManager, NullLogger<TokenEndpoint>.Instance);

        // Act
        Func<Task> act = async () => await handler.HandleAsync(context);

        // Assert
        await act.ShouldNotThrowAsync();
    }

    private static OpenIddictServerEvents.ProcessErrorContext RefusedTokenRequest(OpenIddictRequest request) =>
        new(new OpenIddictServerTransaction
        {
            EndpointType = OpenIddictServerEndpointType.Token,
            Request = request,
            Options = new OpenIddictServerOptions(),
            Logger = NullLogger.Instance
        })
        {
            Error = Errors.InvalidGrant,
            ErrorDescription = "The specified token is invalid."
        };
}
