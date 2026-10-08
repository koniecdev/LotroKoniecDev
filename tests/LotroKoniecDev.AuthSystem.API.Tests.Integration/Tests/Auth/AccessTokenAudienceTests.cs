using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// This API takes only a token that names it (#1023). A service token names the translation API alone,
/// so every endpoint here that takes a bearer token answers it with 401, before any policy or handler
/// runs. Each request carries a body the handler would accept, so only the token can be the reason.
/// </summary>
public sealed class AccessTokenAudienceTests : EndpointsTestBase
{
    private const string Password = "TestPass1!";
    private const string AccountPath = "auth/account/data-export";

    /// <summary>
    /// OpenIddict's own words when a token names none of this API's audiences. Another 401, such as an
    /// expired token, says something else.
    /// </summary>
    private const string NoValidAudience = "doesn't contain any valid audience";

    public AccessTokenAudienceTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    public static TheoryData<string, string> BearerEndpoints => new()
    {
        { "GET", "auth/account/data-export" },
        { "POST", "auth/account/data-export" },
        { "POST", "auth/change-password" },
        { "POST", "auth/account/delete" },
        { "POST", "auth/account/change-email" },
        { "GET", "connect/userinfo" },
        { "POST", "connect/userinfo" }
    };

    [Theory]
    [MemberData(nameof(BearerEndpoints))]
    public async Task BearerEndpoint_ShouldRefuseWithUnauthorized_WhenTheTokenWasIssuedToAService(
        string method,
        string path)
    {
        // Arrange
        string accessToken = await GetClientCredentialsAccessTokenAsync();
        using HttpRequestMessage request = CreateRequest(method, path, accessToken);

        // Act
        using HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        AuthenticationHeaderValue challenge = response.Headers.WwwAuthenticate.ShouldHaveSingleItem();
        challenge.Scheme.ShouldBe("Bearer");
        challenge.Parameter.ShouldNotBeNull().ShouldContain("error=\"invalid_token\"");
        challenge.Parameter.ShouldContain(NoValidAudience);
    }

    [Fact]
    public async Task ClientCredentialsGrant_ShouldNameOnlyTheTranslationApi()
    {
        // Act
        string accessToken = await GetClientCredentialsAccessTokenAsync();

        // Assert
        JwtPayload.ReadAudiences(accessToken).ShouldBe([AuthConstants.ClientIds.Api]);
    }

    [Fact]
    public async Task AccountEndpoint_ShouldRefuseWithUnauthorized_WhenAUserTokenNamesOnlyTheTranslationApi()
    {
        // Arrange: a token from a session that started before #1023
        RegisterRequest user = await RegisterUserAsync();
        await using WebApplicationFactory<Program> host = CreateHostThatIssuesTokensWithoutTheAuthApi();
        using HttpClient client = host.CreateClient();
        (string accessToken, _) = await SignInAsync(client, user.Email);
        JwtPayload.ReadAudiences(accessToken).ShouldBe([AuthConstants.ClientIds.Api]);
        using HttpRequestMessage request = CreateRequest("GET", AccountPath, accessToken);

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ShouldHaveSingleItem().Parameter.ShouldNotBeNull().ShouldContain(NoValidAudience);
    }

    [Fact]
    public async Task RefreshTokenGrant_ShouldNameBothApis_WhenTheSessionStartedWithoutTheAuthApi()
    {
        // Arrange: the refresh token keeps the audiences its session started with, so the refresh must
        // set them itself, or this session would never reach its account again
        RegisterRequest user = await RegisterUserAsync();
        await using WebApplicationFactory<Program> host = CreateHostThatIssuesTokensWithoutTheAuthApi();
        using HttpClient client = host.CreateClient();
        (_, string refreshToken) = await SignInAsync(client, user.Email);

        // Act
        using HttpResponseMessage refreshResponse = await RequestRefreshGrantAsync(client, refreshToken);

        // Assert
        refreshResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument tokens = JsonDocument.Parse(await refreshResponse.Content.ReadAsStringAsync());
        JwtPayload.ReadAudiences(tokens.RootElement.GetProperty("access_token").GetString()!)
            .ShouldBe([AuthConstants.ClientIds.Api, AuthConstants.Audiences.AuthApi], ignoreOrder: true);
    }

    private async Task<RegisterRequest> RegisterUserAsync()
    {
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);

        return user;
    }

    /// <summary>
    /// Signs a user in the way the server did before #1023: the token names the translation API alone. A
    /// refresh is left alone, because that is the code under test. Tokens are signed and sealed with each
    /// host's own keys, so they come from the host that will check them.
    /// </summary>
    private WebApplicationFactory<Program> CreateHostThatIssuesTokensWithoutTheAuthApi() =>
        Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                // A second relay on this database could take a row another test waits for.
                AuthSystemApiFactory.RemoveHostedService<OutboxRelay>(services);

                services.OverrideSignInAudiences(
                    context => context.Request.IsPasswordGrantType(),
                    AuthConstants.ClientIds.Api);
            }));

    private static async Task<(string AccessToken, string RefreshToken)> SignInAsync(HttpClient client, string email)
    {
        using HttpResponseMessage loginResponse = await RequestPasswordGrantAsync(
            client, email, Password, "email profile roles api offline_access");
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using JsonDocument json = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());

        return (
            json.RootElement.GetProperty("access_token").GetString()!,
            json.RootElement.GetProperty("refresh_token").GetString()!);
    }

    private static HttpRequestMessage CreateRequest(string method, string path, string accessToken)
    {
        HttpRequestMessage request = new(new HttpMethod(method), new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = (method, path) switch
        {
            ("GET", _) => null,
            (_, "auth/account/data-export") => JsonContent.Create(new DownloadAccountDataRequest(Password)),
            (_, "auth/change-password") => JsonContent.Create(new ChangePasswordRequest(Password, "NewPass99!")),
            (_, "auth/account/delete") => JsonContent.Create(new DeleteAccountRequest(Password)),
            (_, "auth/account/change-email") => JsonContent.Create(new ChangeEmailRequest("new@example.com", Password)),
            // OpenIddict reads a POST userinfo request as a form, so it refuses one without a form body.
            (_, "connect/userinfo") => new FormUrlEncodedContent([]),
            _ => throw new ArgumentOutOfRangeException(nameof(path), path, "No request body is known for this endpoint.")
        };

        return request;
    }
}
