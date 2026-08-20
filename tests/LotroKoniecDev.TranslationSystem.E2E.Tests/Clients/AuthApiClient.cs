using System.Net.Http.Json;
using System.Text.Json;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.E2E.Tests.Clients.Responses;
using LotroKoniecDev.TranslationSystem.E2E.Tests.Extensions;

namespace LotroKoniecDev.TranslationSystem.E2E.Tests.Clients;

/// <summary>
/// Talks to the running auth-api over real HTTP: registration and the OAuth2 password grant
/// (the <c>lotrokoniecdev-test</c> client, live only under the <c>Testing</c> environment).
/// </summary>
public sealed class AuthApiClient : IDisposable
{
    private const string PasswordGrantScope = "email profile roles api";

    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions;

    public AuthApiClient(string baseUrl, JsonSerializerOptions jsonOptions)
    {
        _client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _jsonOptions = jsonOptions;
    }

    public async Task<IdentityId> RegisterAsync(RegisterRequest request)
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("auth/register", request, _jsonOptions);
        string content = await response.EnsureSuccessWithDetailsAsync();
        return JsonSerializer.Deserialize<IdentityId>(content, _jsonOptions);
    }

    public async Task<HttpResponseMessage> RegisterRawAsync(RegisterRequest request) =>
        await _client.PostAsJsonAsync("auth/register", request, _jsonOptions);

    public async Task<TokenResponse> LoginAsync(string email, string password)
    {
        HttpResponseMessage response = await PostPasswordGrantAsync(email, password);
        await response.EnsureSuccessWithDetailsAsync();
        return await response.Content.ReadFromJsonAsync<TokenResponse>(_jsonOptions)
               ?? throw new InvalidOperationException("Null response from the token endpoint.");
    }

    public async Task<HttpResponseMessage> LoginRawAsync(string email, string password) =>
        await PostPasswordGrantAsync(email, password);

    private async Task<HttpResponseMessage> PostPasswordGrantAsync(string email, string password)
    {
        // "username" is a fixed name in the OIDC protocol. What it carries is the e-mail (ADR-0022).
        Dictionary<string, string> parameters = new()
        {
            ["grant_type"] = "password",
            ["username"] = email,
            ["password"] = password,
            ["client_id"] = E2ETestFixture.TestClientId,
            ["scope"] = PasswordGrantScope
        };

        using FormUrlEncodedContent content = new(parameters);
        return await _client.PostAsync(new Uri("connect/token", UriKind.Relative), content);
    }

    public void Dispose() => _client.Dispose();
}
