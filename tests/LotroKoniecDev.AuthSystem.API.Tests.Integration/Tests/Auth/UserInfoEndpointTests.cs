using System.Net.Http.Headers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
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

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task UserInfo_ShouldRefuseWithInvalidToken_WhenTheTokenWasIssuedToAService(string method)
    {
        // Arrange
        string accessToken = await GetClientCredentialsAccessTokenAsync();

        // Act
        using HttpResponseMessage response = await RequestUserInfoAsync(new HttpMethod(method), accessToken);

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

        await using (AsyncServiceScope scope = Factory.Services.CreateAsyncScope())
        {
            UserManager<ApplicationUser> userManager =
                scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser? user = await userManager.FindByEmailAsync(request.Email);
            user.ShouldNotBeNull();
            (await userManager.DeleteAsync(user)).Succeeded.ShouldBeTrue();
        }

        // Act
        using HttpResponseMessage response = await RequestUserInfoAsync(HttpMethod.Get, accessToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        AuthenticationHeaderValue challenge = response.Headers.WwwAuthenticate.ShouldHaveSingleItem();
        challenge.Scheme.ShouldBe("Bearer");
        challenge.Parameter.ShouldNotBeNull().ShouldContain("error=\"invalid_token\"");
        challenge.Parameter.ShouldContain("error_description=\"The specified access token is invalid.\"");
    }

    private async Task<string> GetClientCredentialsAccessTokenAsync()
    {
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = AuthConstants.ClientIds.Api,
            ["client_secret"] = AuthSystemApiFactory.TestApiClientSecret,
            ["scope"] = "api service"
        });

        using HttpResponseMessage response = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    private async Task<HttpResponseMessage> RequestUserInfoAsync(HttpMethod method, string accessToken)
    {
        using HttpRequestMessage request = new(method, new Uri("connect/userinfo", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // OpenIddict reads a POST userinfo request as a form, so it refuses one without a form body.
        if (method == HttpMethod.Post)
        {
            request.Content = new FormUrlEncodedContent([]);
        }

        return await ApiClient.Http.SendAsync(request);
    }
}
