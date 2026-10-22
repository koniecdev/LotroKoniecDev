using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.ErrorContract;

/// <summary>
/// Pins the API's error contract at the HTTP level; the other suites only check the status code.
/// Domain failures are served as <c>application/problem+json</c> carrying a typed <c>errorCode</c>, a
/// status-family <c>title</c> and the <c>status</c> (ErrorExtensions.ToProblemDetails: 400 Validation,
/// 404 NotFound, 422 DataConflict). Auth rejections (401/403) are also served as
/// <c>application/problem+json</c> but carry no domain <c>errorCode</c> extension; 409 has no path
/// (no optimistic-concurrency token is configured).
/// </summary>
[Collection("TranslationApi")]
public sealed class ProblemDetailsContractTests : IAsyncLifetime
{
    private const string ProblemJson = "application/problem+json";

    private readonly TranslationSystemApiFactory _factory;

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"TranslationArtifacts\", translation.\"Translators\" CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ListTranslations_WithUnsupportedLanguage_ShouldReturn400ValidationProblem()
    {
        // Arrange
        using HttpClient client = TranslatorClient();

        // Act
        HttpResponseMessage response = await client.GetAsync("/api/v1/translations?lang=de");
        JsonElement problem = await ReadProblemDetailsAsync(response);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe(ProblemJson);
        problem.GetProperty("status").GetInt32().ShouldBe(400);
        problem.GetProperty("title").GetString().ShouldBe("Validation Error");
        problem.GetProperty("errorCode").GetString().ShouldBe("Translations.UnsupportedLanguage");
    }

    [Fact]
    public async Task RegisterGameVersion_WithInvalidFormat_ShouldReturn400ValidationProblem()
    {
        // Arrange
        using HttpClient client = AdminClient();

        // Act: "banana" passes the NotEmpty/MaxLength validator, so the failure surfaces from the
        // LotroNotationVersion value object's dotted-numeric format check.
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/game-versions", new RegisterGameVersionRequest("banana"));
        JsonElement problem = await ReadProblemDetailsAsync(response);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe(ProblemJson);
        problem.GetProperty("title").GetString().ShouldBe("Validation Error");
        problem.GetProperty("errorCode").GetString().ShouldBe("GameVersionEntity.LotroNotationVersion.InvalidFormat");
    }

    [Fact]
    public async Task UpsertTranslation_WithEmptyText_ShouldReturn400ValidationProblem()
    {
        // Arrange
        using HttpClient client = TranslatorClient();

        // Act
        HttpResponseMessage response = await client.PutAsJsonAsync(
            "/api/v1/translations", new UpsertTranslationRequest(620756992, 1, "   "));
        JsonElement problem = await ReadProblemDetailsAsync(response);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        problem.GetProperty("title").GetString().ShouldBe("Validation Error");
        problem.GetProperty("errorCode").GetString().ShouldBe("Translations.Validation");
    }

    [Theory]
    [InlineData("pl-PL")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public async Task UpsertTranslation_WithEmptyTextOnAServerInAnotherLanguage_ShouldAnswerInEnglish(string serverCulture)
    {
        // Arrange: on a server whose OS language is not English, every request runs in that culture
        // (#1092).
        await using WebApplicationFactory<Program> host =
            HostWithRequestCulture(CultureInfo.GetCultureInfo(serverCulture));
        using HttpClient client = ClientForRole(host, AuthConstants.Roles.Translator);

        // Act
        HttpResponseMessage response = await client.PutAsJsonAsync(
            "/api/v1/translations", new UpsertTranslationRequest(620756992, 1, "   "));
        JsonElement problem = await ReadProblemDetailsAsync(response);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        problem.GetProperty("detail").GetString().ShouldBe("'Translated Text' must not be empty.");
    }

    [Fact]
    public async Task GetTranslation_WithUnknownId_ShouldReturn404NotFoundProblem()
    {
        // Arrange
        using HttpClient client = TranslatorClient();

        // Act
        HttpResponseMessage response = await client.GetAsync($"/api/v1/translations/{Guid.NewGuid()}");
        JsonElement problem = await ReadProblemDetailsAsync(response);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe(ProblemJson);
        problem.GetProperty("status").GetInt32().ShouldBe(404);
        problem.GetProperty("title").GetString().ShouldBe("Not Found");
        problem.GetProperty("errorCode").GetString().ShouldBe("TranslationEntity.NotFound");
    }

    [Fact]
    public async Task GetGameVersion_WithUnknownId_ShouldReturn404NotFoundProblem()
    {
        // Arrange
        using HttpClient client = TranslatorClient();

        // Act
        HttpResponseMessage response = await client.GetAsync($"/api/v1/game-versions/{Guid.NewGuid()}");
        JsonElement problem = await ReadProblemDetailsAsync(response);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        problem.GetProperty("title").GetString().ShouldBe("Not Found");
        problem.GetProperty("errorCode").GetString().ShouldBe("GameVersionEntity.NotFound");
    }

    [Fact]
    public async Task RegisterGameVersion_WhenAlreadyTaken_ShouldReturn422DataConflictProblem()
    {
        // Arrange: register the version, then re-register an equivalent notation.
        using HttpClient client = AdminClient();
        (await client.PostAsJsonAsync("/api/v1/game-versions", new RegisterGameVersionRequest("48.0")))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/game-versions", new RegisterGameVersionRequest("48.0.0"));
        JsonElement problem = await ReadProblemDetailsAsync(response);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe(ProblemJson);
        problem.GetProperty("status").GetInt32().ShouldBe(422);
        problem.GetProperty("title").GetString().ShouldBe("Data Conflict");
        problem.GetProperty("errorCode").GetString().ShouldBe("GameVersionEntity.LotroNotationVersion.AlreadyTaken");
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_ShouldReturn401AsProblemDetailsWithoutErrorCode()
    {
        // Arrange: /api/v1/game-versions: the translations list itself is publicly readable (#309).
        using HttpClient client = _factory.CreateClient();

        // Act
        HttpResponseMessage response = await client.GetAsync("/api/v1/game-versions");
        JsonElement problem = await ReadProblemDetailsAsync(response);

        // Assert: authentication failures share the problem+json surface, but the domain errorCode
        // extension is added only by ErrorExtensions.ToProblemDetails for Result failures, never here.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe(ProblemJson);
        problem.GetProperty("status").GetInt32().ShouldBe(401);
        problem.TryGetProperty("errorCode", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task AdminEndpoint_AsTranslator_ShouldReturn403AsProblemDetailsWithoutErrorCode()
    {
        // Arrange
        using HttpClient client = TranslatorClient();

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/game-versions", new RegisterGameVersionRequest("48.0"));
        JsonElement problem = await ReadProblemDetailsAsync(response);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe(ProblemJson);
        problem.GetProperty("status").GetInt32().ShouldBe(403);
        problem.TryGetProperty("errorCode", out _).ShouldBeFalse();
    }

    public ProblemDetailsContractTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    private static async Task<JsonElement> ReadProblemDetailsAsync(HttpResponseMessage response)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.Clone();
    }

    private static HttpClient ClientForRole(WebApplicationFactory<Program> host, string role)
    {
        HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(role));
        return client;
    }

    private HttpClient TranslatorClient() => ClientForRole(_factory, AuthConstants.Roles.Translator);

    private HttpClient AdminClient() => ClientForRole(_factory, AuthConstants.Roles.Admin);

    private WebApplicationFactory<Program> HostWithRequestCulture(CultureInfo culture)
    {
        return _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter>(new RequestCultureStartupFilter(culture))));
    }

    /// <summary>
    /// Runs every request in the given culture, as a server whose OS has that language does. Without
    /// it, the test server takes the culture of the machine that runs the tests.
    /// </summary>
    private sealed class RequestCultureStartupFilter : IStartupFilter
    {
        private readonly CultureInfo _culture;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    CultureInfo.CurrentCulture = _culture;
                    CultureInfo.CurrentUICulture = _culture;
                    await nextMiddleware(context);
                });
                next(app);
            };
        }

        public RequestCultureStartupFilter(CultureInfo culture)
        {
            _culture = culture;
        }
    }
}
