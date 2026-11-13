using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using LotroKoniecDev.Frontend.Tests.Integration.Shared;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.Discovery;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.GameVersions;

/// <summary>
/// #549: a delete needs a second click on a question the page draws only at the address that names the row,
/// and a successful delete redirects. bUnit calls a form's handler directly, so only the real host shows
/// which address the form posts to and that the posted form is found there.
/// </summary>
public sealed partial class GameVersionsDeleteConfirmationTests : IClassFixture<ProductionFrontendFactory>
{
    private const string TmsOrigin = "https://tms.staging.invalid";
    private const string DeletableVersionId = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
    private const string ProcessedVersionId = "6fa459ea-ee8a-3ca4-894e-db77e160355e";
    private const string ConfirmDeleteUri = $"/game-versions?confirm-delete={DeletableVersionId}";
    private const string DeleteFormName = $"delete-{DeletableVersionId}";
    private const string DeletedMessage = "Usunięto wersję gry.";

    /// <summary>The Polish text a 503 from an API turns into (ApiProblemCopy).</summary>
    private const string ServiceUnavailableMessage = "Usługa jest chwilowo niedostępna. Spróbuj ponownie za chwilę.";

    /// <summary>The names ASP.NET Core gives the typed clients, which are internal to the Frontend.</summary>
    private const string TranslationSystemClientName = "ITranslationSystemClient";
    private const string AuthSystemClientName = "IAuthSystemClient";

    private static readonly Uri GameVersionsHref = new($"{TmsOrigin}/advertised/game-versions");
    private static readonly Uri DeleteHref = new($"{TmsOrigin}/advertised/delete/{DeletableVersionId}");

    private readonly ProductionFrontendFactory _factory;

    [Fact]
    public async Task Get_ThePlainPage_ShowsTheDeleteLinkAndNoDeleteForm()
    {
        // Arrange
        StubTranslationSystem translationSystem = new();
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);

        // Act
        using HttpResponseMessage response = await browser.GetAsync("/game-versions");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        page.ShouldContain($"href=\"{ConfirmDeleteUri}\"");
        page.ShouldNotContain(DeleteFormName);
        translationSystem.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task Get_TheConfirmAddress_AsksAQuestionThatNamesTheVersionAndSendsNoWrite()
    {
        // Arrange
        StubTranslationSystem translationSystem = new();
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);

        // Act
        using HttpResponseMessage response = await browser.GetAsync(ConfirmDeleteUri);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        page.ShouldContain("Na pewno usunąć wersję 48.0?");
        page.ShouldContain(DeleteFormName);
        translationSystem.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task Post_TheConfirmFormToTheAddressItNames_DeletesOnceThroughTheAdvertisedHrefAndRedirects()
    {
        // Arrange: the form has no action of its own, so Blazor writes the address it was drawn at. Only
        // that address draws the form again on the post; anything else gets Blazor's blank 400.
        StubTranslationSystem translationSystem = new();
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        DeleteForm deleteForm = await ReadDeleteFormAsync(browser);
        string antiforgeryToken = deleteForm.AntiforgeryToken.ShouldNotBeNull();

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(
            FormPost(deleteForm.Action, DeleteFormName, antiforgeryToken));

        // Assert
        new Uri(new Uri($"https://localhost{ConfirmDeleteUri}"), deleteForm.Action).PathAndQuery
            .ShouldBe(ConfirmDeleteUri);
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        posted.Headers.Location.ShouldNotBeNull().OriginalString.ShouldEndWith("/game-versions?deleted=true");
        translationSystem.Deletes.ShouldBe([DeleteHref]);
    }

    [Fact]
    public async Task Get_TheRedirectAfterADelete_ShowsTheConfirmationAndTheListWithoutTheRowAndSendsNoDelete()
    {
        // Arrange
        StubTranslationSystem translationSystem = new();
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        DeleteForm deleteForm = await ReadDeleteFormAsync(browser);
        using HttpResponseMessage posted = await browser.SendAsync(
            FormPost(deleteForm.Action, DeleteFormName, deleteForm.AntiforgeryToken.ShouldNotBeNull()));
        Uri redirectedTo = posted.Headers.Location
            ?? throw new InvalidOperationException($"The delete answered {posted.StatusCode} with no redirect.");

        // Act
        using HttpResponseMessage redirected = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Get, redirectedTo));

        // Assert: a reload is this same GET, so it can never send the delete again.
        redirected.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await redirected.Content.ReadAsStringAsync());
        page.ShouldContain(DeletedMessage);
        page.ShouldNotContain("<td class=\"mono\">48.0</td>");
        page.ShouldContain("<td class=\"mono\">47.2</td>");
        translationSystem.Deletes.ShouldBe([DeleteHref]);
    }

    [Fact]
    public async Task Post_TheDeleteFormToThePlainAddress_SendsNoDelete()
    {
        // Arrange: the plain page never draws the form. What such a post gets back is #1164's to decide.
        StubTranslationSystem translationSystem = new();
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(FormPost("/game-versions", DeleteFormName, antiforgeryToken));

        // Assert
        translationSystem.Deletes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_TheConfirmForm_WhenTheVersionListFailsToLoad_ShowsTheLoadErrorAndSendsNoDelete()
    {
        // Arrange
        StubTranslationSystem translationSystem = new() { VersionListFails = true };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(FormPost(ConfirmDeleteUri, DeleteFormName, antiforgeryToken));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.ShouldContain("<h1>Wersje gry</h1>");
        page.ShouldContain(ServiceUnavailableMessage);
        translationSystem.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task Post_TheRegisterFormToTheDeletedAddress_WhenTheVersionListFailsToLoad_ShowsNoOldConfirmation()
    {
        // Arrange: the register posts to the address the delete's redirect left, so the old flag must not
        // read as its answer (#1122).
        StubTranslationSystem translationSystem = new() { VersionListFails = true };
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(
            FormPost("/game-versions?deleted=true", "register-version", antiforgeryToken));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.ShouldContain(ServiceUnavailableMessage);
        page.ShouldNotContain(DeletedMessage);
        translationSystem.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task Post_TheRegisterFormToTheDeletedAddress_ShowsOnlyTheRegisterConfirmation()
    {
        // Arrange
        StubTranslationSystem translationSystem = new();
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        string antiforgeryToken = await browser.ReadAntiforgeryTokenAsync("/terms");

        // Act
        using HttpResponseMessage posted = await browser.SendAsync(FormPost(
            "/game-versions?deleted=true",
            "register-version",
            antiforgeryToken,
            new Dictionary<string, string> { ["RegisterInput.Version"] = "49.0" }));

        // Assert
        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync());
        page.ShouldContain("Zarejestrowano wersję 49.0.");
        page.ShouldNotContain(DeletedMessage);
        translationSystem.Registered.ShouldBe(["49.0"]);
    }

    [Theory]
    [InlineData("/game-versions?confirm-delete=zzz")]
    [InlineData("/game-versions?deleted=zzz")]
    public async Task Get_WithAQueryValueThePageCannotRead_ShowsThePlainList(string pathAndQuery)
    {
        // Arrange: both values are read as text, so a bad one counts as absent instead of failing the page
        // (#1356).
        StubTranslationSystem translationSystem = new();
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);

        // Act
        using HttpResponseMessage response = await browser.GetAsync(pathAndQuery);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        page.ShouldContain($"href=\"{ConfirmDeleteUri}\"");
        page.ShouldNotContain("Na pewno usunąć wersję");
        page.ShouldNotContain(DeletedMessage);
    }

    public GameVersionsDeleteConfirmationTests(ProductionFrontendFactory factory)
    {
        _factory = factory;
    }

    [GeneratedRegex("""<form\b(?<attributes>[^>]*)>(?<body>.*?)</form>""", RegexOptions.Singleline)]
    private static partial Regex FormElement();

    [GeneratedRegex("""\baction="(?<action>[^"]*)""")]
    private static partial Regex ActionAttribute();

    [GeneratedRegex("""name="__RequestVerificationToken" value="(?<token>[^"]+)""")]
    private static partial Regex AntiforgeryTokenInput();

    /// <summary>
    /// The delete form as the browser gets it at the confirm address. With no <c>action</c> a browser posts
    /// to the address it is on.
    /// </summary>
    private static async Task<DeleteForm> ReadDeleteFormAsync(CookieJarBrowser browser)
    {
        using HttpResponseMessage response = await browser.GetAsync(ConfirmDeleteUri);
        string page = await response.Content.ReadAsStringAsync();

        Match form = FormElement().Matches(page)
            .FirstOrDefault(match => match.Groups["body"].Value.Contains($"value=\"{DeleteFormName}\"", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"The confirm address answered {response.StatusCode} with no delete form.");
        Match action = ActionAttribute().Match(form.Groups["attributes"].Value);
        Match antiforgeryToken = AntiforgeryTokenInput().Match(form.Groups["body"].Value);

        return new DeleteForm(
            action.Success ? WebUtility.HtmlDecode(action.Groups["action"].Value) : ConfirmDeleteUri,
            antiforgeryToken.Success ? antiforgeryToken.Groups["token"].Value : null);
    }

    private WebApplicationFactory<Program> CreateHost(StubTranslationSystem translationSystem) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            // Registered after the app's own setup, so these win. Nothing goes to the network.
            services.Configure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                options => options.BackchannelHttpHandler = new UnreachableServer());
            services.AddHttpClient(TranslationSystemClientName)
                .ConfigurePrimaryHttpMessageHandler(() => translationSystem);
            services.AddHttpClient(AuthSystemClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new UnreachableServer());
        }));

    private static HttpRequestMessage FormPost(
        string pathAndQuery,
        string formName,
        string antiforgeryToken,
        Dictionary<string, string>? fields = null)
    {
        Dictionary<string, string> form = fields is null ? new() : new(fields);
        form["_handler"] = formName;
        form["__RequestVerificationToken"] = antiforgeryToken;

        return new HttpRequestMessage(HttpMethod.Post, pathAndQuery) { Content = new FormUrlEncodedContent(form) };
    }

    private sealed record DeleteForm(string Action, string? AntiforgeryToken);

    /// <summary>
    /// An admin's list: one row the admin may delete and one Processed row without the delete rel. A delete
    /// removes the row, and the list can be set to answer 503, as it does while the API is down.
    /// </summary>
    private sealed class StubTranslationSystem : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Uri> _deletes = new();
        private readonly ConcurrentQueue<string> _registered = new();
        private int _writes;

        public bool VersionListFails { get; init; }

        /// <summary>How many requests other than a read reached the API.</summary>
        public int Writes => _writes;

        public IReadOnlyList<Uri> Deletes => [.. _deletes];

        public IReadOnlyList<string> Registered => [.. _registered];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("A call with no address.");
            if (request.Method != HttpMethod.Get)
            {
                Interlocked.Increment(ref _writes);
            }

            if (uri.GetLeftPart(UriPartial.Path) == $"{TmsOrigin}/")
            {
                return StubAnswers.HateoasJson(new DiscoveryResponse("tms")
                {
                    Links =
                    [
                        new LinkDto(GameVersionsHref.AbsoluteUri, Rels.GameVersions, "GET"),
                        new LinkDto($"{TmsOrigin}/advertised/contributions", Rels.ContributionDataExport, "GET")
                    ]
                });
            }

            if (uri == GameVersionsHref && request.Method == HttpMethod.Get)
            {
                return LoadGameVersions();
            }

            if (uri == GameVersionsHref && request.Method == HttpMethod.Post)
            {
                return await RegisterAsync(request, cancellationToken);
            }

            if (uri == DeleteHref && request.Method == HttpMethod.Delete)
            {
                _deletes.Enqueue(uri);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private HttpResponseMessage LoadGameVersions()
        {
            if (VersionListFails)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            List<GameVersionResponse> items = [];
            if (_deletes.IsEmpty)
            {
                items.Add(new GameVersionResponse(
                    GameVersionId.Create(Guid.Parse(DeletableVersionId)),
                    "48.0",
                    DateTimeOffset.UnixEpoch,
                    GameVersionStatus.Unprocessed)
                {
                    Links = [new LinkDto(DeleteHref.AbsoluteUri, Rels.Delete, "DELETE")]
                });
            }

            items.Add(new GameVersionResponse(
                GameVersionId.Create(Guid.Parse(ProcessedVersionId)),
                "47.2",
                DateTimeOffset.UnixEpoch,
                GameVersionStatus.Processed));

            return StubAnswers.HateoasJson(new CollectionResponse<GameVersionResponse>
            {
                Items = items,
                Links = [new LinkDto(GameVersionsHref.AbsoluteUri, Rels.Register, "POST")]
            });
        }

        private async Task<HttpResponseMessage> RegisterAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RegisterGameVersionRequest register = await request.Content.ShouldNotBeNull()
                .ReadFromJsonAsync<RegisterGameVersionRequest>(cancellationToken)
                ?? throw new InvalidOperationException("A register with no body.");
            _registered.Enqueue(register.Version);

            return StubAnswers.HateoasJson(new GameVersionResponse(
                GameVersionId.Create(Guid.NewGuid()),
                register.Version,
                DateTimeOffset.UnixEpoch,
                GameVersionStatus.Unprocessed));
        }
    }
}
