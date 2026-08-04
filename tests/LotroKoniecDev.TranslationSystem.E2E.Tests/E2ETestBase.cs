using System.Text.Json;
using System.Text.Json.Serialization;
using Bogus;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.E2E.Tests.Clients;
using LotroKoniecDev.TranslationSystem.E2E.Tests.Clients.Responses;

namespace LotroKoniecDev.TranslationSystem.E2E.Tests;

[Collection("E2E")]
public abstract class E2ETestBase : IAsyncLifetime
{
    protected static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string TranslatorPassword = "TranslatorPass123!";

    private readonly List<IDisposable> _disposables = [];

    protected E2ETestFixture Fixture { get; }
    protected AuthApiClient AuthApi { get; }
    protected Faker Faker { get; } = new();

    protected E2ETestBase(E2ETestFixture fixture)
    {
        Fixture = fixture;
        AuthApi = new AuthApiClient(fixture.AuthApiBaseUrl, JsonOptions);
        _disposables.Add(AuthApi);
    }

    /// <summary>Each test starts from an empty translation catalog so row counts and file ETags are deterministic.</summary>
    public virtual async Task InitializeAsync() => await Fixture.ResetTranslationDataAsync();

    /// <summary>Creates a tms-api client carrying the given bearer token (anonymous when null); disposed with the test.</summary>
    protected TranslationSystemApiClient CreateTmsClient(string? bearerToken = null)
    {
        TranslationSystemApiClient client = new(Fixture.TmsApiBaseUrl, JsonOptions, bearerToken);
        _disposables.Add(client);
        return client;
    }

    /// <summary>Logs in the seeded admin (Admin role, email pre-confirmed) via the password grant and returns its access token.</summary>
    protected async Task<string> LoginAsAdminAsync()
    {
        TokenResponse token = await AuthApi.LoginAsync(E2ETestFixture.AdminEmail, E2ETestFixture.AdminPassword);
        return token.AccessToken;
    }

    /// <summary>
    /// Registers a fresh user (granted the Translator role), confirms its e-mail through the
    /// fixture's database seam (this network has no broker, so the confirmation e-mail never
    /// arrives) and logs it in.
    /// </summary>
    protected async Task<RegisteredTranslator> RegisterAndLoginTranslatorAsync()
    {
        string username = $"translator{Faker.Random.AlphaNumeric(10)}";
        string email = $"{username}@lotro-translator.pl";

        RegisterRequest request = new(
            username,
            email,
            TranslatorPassword,
            AcceptedPrivacyPolicy: true,
            AcceptedDataProcessingConsent: true,
            AcceptedTermsOfService: true);

        IdentityId identityId = await AuthApi.RegisterAsync(request);
        await Fixture.ConfirmUserEmailAsync(email);
        TokenResponse token = await AuthApi.LoginAsync(email, TranslatorPassword);
        return new RegisteredTranslator(identityId, username, email, token.AccessToken);
    }

    public virtual Task DisposeAsync()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        return Task.CompletedTask;
    }

    protected sealed record RegisteredTranslator(IdentityId IdentityId, string Username, string Email, string AccessToken);
}
