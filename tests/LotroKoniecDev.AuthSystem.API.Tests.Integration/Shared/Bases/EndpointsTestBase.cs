using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using LotroKoniecDev.SharedKernel.Authorization;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;

[Collection("AuthApi")]
public abstract class EndpointsTestBase : AsyncLifetimeTestBase
{
    protected override TestApiClient ApiClient { get; }

    protected EndpointsTestBase(AuthSystemApiFactory appFactory) : base(appFactory)
    {
        JsonSerializerOptions jsonSerializerOptions =
            appFactory.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

        ApiClient = new TestApiClient(appFactory.CreateClient(), jsonSerializerOptions);
    }

    protected async Task<string> GetAccessTokenAsync(string email, string password)
    {
        HttpResponseMessage tokenResponse = await RequestPasswordGrantAsync(email, password);

        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string content = await tokenResponse.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>
    /// A token issued to the API client itself. Its subject is the client id, not a user id.
    /// </summary>
    protected async Task<string> GetClientCredentialsAccessTokenAsync()
    {
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = AuthConstants.ClientIds.Api,
            ["client_secret"] = AuthSystemApiFactory.TestApiClientSecret,
            ["scope"] = "api service"
        });

        using HttpResponseMessage tokenResponse =
            await ApiClient.Http.PostAsync(new Uri("connect/token", UriKind.Relative), tokenRequest);

        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string content = await tokenResponse.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    protected Task<string> GetRefreshTokenAsync(string email, string password) =>
        GetRefreshTokenAsync(ApiClient.Http, email, password);

    /// <summary>
    /// Takes the client of the host that will check the token. Tokens are sealed with each host's own keys.
    /// </summary>
    protected static async Task<string> GetRefreshTokenAsync(HttpClient client, string email, string password)
    {
        using HttpResponseMessage tokenResponse =
            await RequestPasswordGrantAsync(client, email, password, "email profile roles api offline_access");

        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string content = await tokenResponse.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);
        return json.RootElement.GetProperty("refresh_token").GetString()!;
    }

    protected Task<HttpResponseMessage> RequestRefreshGrantAsync(string refreshToken) =>
        RequestRefreshGrantAsync(ApiClient.Http, refreshToken);

    protected static async Task<HttpResponseMessage> RequestRefreshGrantAsync(HttpClient client, string refreshToken)
    {
        using FormUrlEncodedContent refreshRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = "lotrokoniecdev-test"
        });

        return await client.PostAsync(new Uri("connect/token", UriKind.Relative), refreshRequest);
    }

    protected Task<HttpResponseMessage> RequestPasswordGrantAsync(
        string email,
        string password,
        string scope = "email profile roles api") =>
        RequestPasswordGrantAsync(ApiClient.Http, email, password, scope);

    private static async Task<HttpResponseMessage> RequestPasswordGrantAsync(
        HttpClient client,
        string email,
        string password,
        string scope)
    {
        // "username" is a fixed name in the OIDC protocol. What it carries is the e-mail (ADR-0022).
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = email,
            ["password"] = password,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = scope
        });

        return await client.PostAsync(new Uri("connect/token", UriKind.Relative), tokenRequest);
    }
}
