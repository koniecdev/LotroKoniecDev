using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// A client token's subject is the client id, so the account endpoints refuse it with the policy's 403
/// before any handler runs (#966). A real service token gets a 401 first (#1023), so the host gives it this
/// API's audience too. Each request carries a valid body, so only the policy can be the reason.
/// </summary>
public sealed class UserTokenPolicyTests : EndpointsTestBase
{
    private const string Password = "TestPass1!";

    public UserTokenPolicyTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    public static TheoryData<string, string> AccountEndpoints => new()
    {
        { "GET", "auth/account/data-export" },
        { "POST", "auth/account/data-export" },
        { "POST", "auth/change-password" },
        { "POST", "auth/account/delete" },
        { "POST", "auth/account/change-email" }
    };

    [Theory]
    [MemberData(nameof(AccountEndpoints))]
    public async Task AccountEndpoint_ShouldRefuseWithForbidden_WhenAClientTokenNamesThisApi(
        string method,
        string path)
    {
        // Arrange
        await using WebApplicationFactory<Program> host = CreateHostThatGivesClientTokensThisApi();
        using HttpClient client = host.CreateClient();
        string accessToken = await GetClientCredentialsAccessTokenAsync(client);
        using HttpRequestMessage request = CreateRequest(method, path, accessToken);

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        AuthenticationHeaderValue challenge = response.Headers.WwwAuthenticate.ShouldHaveSingleItem();
        challenge.Scheme.ShouldBe("Bearer");
        challenge.Parameter.ShouldNotBeNull().ShouldContain("error=\"insufficient_access\"");
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/problem+json");
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetInt32().ShouldBe(403);
        body.RootElement.GetProperty("title").GetString().ShouldBe("Forbidden");
        body.RootElement.TryGetProperty("errorCode", out _).ShouldBeFalse();
    }

    /// <summary>
    /// Tokens are signed with each host's own keys, so they come from the host that will check them.
    /// </summary>
    private WebApplicationFactory<Program> CreateHostThatGivesClientTokensThisApi() =>
        Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                // A second relay on this database could take a row another test waits for.
                AuthSystemApiFactory.RemoveHostedService<OutboxRelay>(services);

                services.OverrideSignInAudiences(
                    context => context.Request.IsClientCredentialsGrantType(),
                    AuthConstants.ClientIds.Api,
                    AuthConstants.Audiences.AuthApi);
            }));

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
            _ => throw new ArgumentOutOfRangeException(nameof(path), path, "No request body is known for this endpoint.")
        };

        return request;
    }
}
