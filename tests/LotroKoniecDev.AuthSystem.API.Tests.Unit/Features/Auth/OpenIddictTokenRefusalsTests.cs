using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.Tests.Shared;
using Microsoft.Extensions.Logging;
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
        OpenIddictServerEvents.ProcessErrorContext context = RefusedTokenRequest(grantType);
        OpenIddictTokenRefusals.WarnWhenRefused handler = new(_tokenManager, NullLogger<TokenEndpoint>.Instance);

        // Act
        Func<Task> act = async () => await handler.HandleAsync(context);

        // Assert
        await act.ShouldNotThrowAsync();
    }

    [Theory]
    [InlineData(GrantTypes.RefreshToken, "Refresh")]
    [InlineData(GrantTypes.AuthorizationCode, "Code exchange")]
    public async Task WarnWhenRefused_WhenTheStoredTokenCannotBeRead_ShouldWarnWithTheFailure(
        string grantType,
        string expectedStep)
    {
        // Arrange
        InvalidOperationException failure = new("The database is unreachable.");
        _tokenManager.FindByReferenceIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        using CapturingLoggerFactory loggerFactory = new();
        OpenIddictTokenRefusals.WarnWhenRefused handler = new(_tokenManager, new Logger<TokenEndpoint>(loggerFactory));

        // Act
        await handler.HandleAsync(RefusedTokenRequest(grantType));

        // Assert
        CapturingLoggerFactory.LogEntry warning = loggerFactory.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.Id.ShouldBe(EventIds.TokenGrantRefusalUserLookupFailed);
        warning.Message.ShouldBe($"{expectedStep} refused by OpenIddict, and the stored token could not be read to name its user");
        warning.Exception.ShouldBeSameAs(failure);
    }

    private static OpenIddictServerEvents.ProcessErrorContext RefusedTokenRequest(string grantType) =>
        new(new OpenIddictServerTransaction
        {
            EndpointType = OpenIddictServerEndpointType.Token,
            Request = new OpenIddictRequest
            {
                GrantType = grantType,
                Code = "stored-code-id",
                RefreshToken = "stored-refresh-token-id"
            },
            Options = new OpenIddictServerOptions(),
            Logger = NullLogger.Instance
        })
        {
            Error = Errors.InvalidGrant,
            ErrorDescription = "The specified token is invalid."
        };
}
