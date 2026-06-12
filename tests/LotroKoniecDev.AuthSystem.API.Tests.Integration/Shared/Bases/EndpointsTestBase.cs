using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;

[Collection("AuthApi")]
public abstract class EndpointsTestBase : AsyncLifetimeTestBase
{
    protected override TestApiClient ApiClient { get; }

    protected EndpointsTestBase(AuthSystemApiFactory appFactory) : base(appFactory)
    {
        JsonSerializerOptions jsonSerializerOptions =
            appFactory.Services.GetRequiredService<IOptionsSnapshot<JsonOptions>>().Value.SerializerOptions;

        ApiClient = new TestApiClient(appFactory.CreateClient(), jsonSerializerOptions);
    }

    protected async Task<string> GetAccessTokenAsync(string username, string password)
    {
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = password,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = "email profile roles api"
        });

        HttpResponseMessage tokenResponse = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);

        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string content = await tokenResponse.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }
}
