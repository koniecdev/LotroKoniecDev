using System.Net.Http.Headers;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// The account endpoints act on the caller's own account. A service token is valid, but its subject is the
/// client id, so these endpoints refuse it with the 403 a failed policy gives, before any handler runs
/// (#966). Each request carries a body the handler would accept: without the policy, data export answered
/// 500 and the others 404 <c>Auth.UserNotFound</c>.
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
    public async Task AccountEndpoint_ShouldRefuseWithForbidden_WhenTheTokenWasIssuedToAService(
        string method,
        string path)
    {
        // Arrange
        string accessToken = await GetClientCredentialsAccessTokenAsync();
        using HttpRequestMessage request = CreateRequest(method, path, accessToken);

        // Act
        using HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

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
