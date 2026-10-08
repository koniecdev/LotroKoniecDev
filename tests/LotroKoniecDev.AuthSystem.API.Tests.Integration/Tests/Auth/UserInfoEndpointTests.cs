using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed class UserInfoEndpointTests : EndpointsTestBase
{
    private const string Password = "TestPass1!";

    public UserInfoEndpointTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task UserInfo_ShouldReturnTheUsersClaims_WhenTheTokenBelongsToAUser()
    {
        // Arrange
        (RegisterRequest request, IdentityId userId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        string accessToken = await GetAccessTokenAsync(request.Email, Password);

        // Act
        using HttpResponseMessage response = await RequestUserInfoAsync(HttpMethod.Get, accessToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("sub").GetString().ShouldBe(userId.Value.ToString());
        body.RootElement.GetProperty("email").GetString().ShouldBe(request.Email);
        body.RootElement.GetProperty("email_verified").GetBoolean().ShouldBeTrue();
        body.RootElement.GetProperty("name").GetString().ShouldBe(request.Username);
        body.RootElement.GetProperty("role").EnumerateArray().Select(role => role.GetString()).ShouldBe(["Translator"]);
    }

    /// <summary>
    /// A real service token does not name this API, so the audience check refuses it first (#1023). The host
    /// here gives it this API's audience too, so the token reaches the handler, which must not hand the
    /// client id to Identity (#955).
    /// </summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task UserInfo_ShouldRefuseWithInvalidToken_WhenAClientTokenNamesThisApi(string method)
    {
        // Arrange
        await using WebApplicationFactory<Program> host = Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                // A second relay on this database could take a row another test waits for.
                AuthSystemApiFactory.RemoveHostedService<OutboxRelay>(services);

                services.OverrideSignInAudiences(
                    context => context.Request.IsClientCredentialsGrantType(),
                    AuthConstants.ClientIds.Api,
                    AuthConstants.Audiences.AuthApi);
            }));
        using HttpClient client = host.CreateClient();
        string accessToken = await GetClientCredentialsAccessTokenAsync(client);

        // Act
        using HttpResponseMessage response = await RequestUserInfoAsync(client, new HttpMethod(method), accessToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        AuthenticationHeaderValue challenge = response.Headers.WwwAuthenticate.ShouldHaveSingleItem();
        challenge.Scheme.ShouldBe("Bearer");
        challenge.Parameter.ShouldNotBeNull().ShouldContain("error=\"invalid_token\"");
        challenge.Parameter.ShouldContain("error_description=\"The specified access token is invalid.\"");
    }

    [Fact]
    public async Task UserInfo_ShouldRefuseWithInvalidToken_WhenTheUserNoLongerExists()
    {
        // Arrange
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        string accessToken = await GetAccessTokenAsync(request.Email, Password);

        await AccountStateFactory.DeleteAsync(Factory.Services, request.Email);

        // Act
        using HttpResponseMessage response = await RequestUserInfoAsync(HttpMethod.Get, accessToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        AuthenticationHeaderValue challenge = response.Headers.WwwAuthenticate.ShouldHaveSingleItem();
        challenge.Scheme.ShouldBe("Bearer");
        challenge.Parameter.ShouldNotBeNull().ShouldContain("error=\"invalid_token\"");
        challenge.Parameter.ShouldContain("error_description=\"The specified access token is invalid.\"");
    }

    private Task<HttpResponseMessage> RequestUserInfoAsync(HttpMethod method, string accessToken) =>
        RequestUserInfoAsync(ApiClient.Http, method, accessToken);

    private static async Task<HttpResponseMessage> RequestUserInfoAsync(
        HttpClient client,
        HttpMethod method,
        string accessToken)
    {
        using HttpRequestMessage request = new(method, new Uri("connect/userinfo", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // OpenIddict reads a POST userinfo request as a form, so it refuses one without a form body.
        if (method == HttpMethod.Post)
        {
            request.Content = new FormUrlEncodedContent([]);
        }

        return await client.SendAsync(request);
    }
}
