using System.Net;
using System.Text.Json;
using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Tests.Unit.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Errors;

/// <summary>
/// The Polish-copy lookup for API failures (#548 / ADR-0044). The contract under test: an
/// API-authored problem is keyed by its <c>errorCode</c> and its English wording never becomes the
/// message, while a Frontend-authored problem (no code, already Polish) passes through untouched.
/// </summary>
public sealed class ApiProblemCopyTests
{
    private const string PageFallback = "Nie udało się wczytać listy wersji gry.";

    [Fact]
    public void Describe_WhenThereIsNoProblem_UsesTheCallSiteFallback()
    {
        ApiProblemView view = ApiProblemCopy.Describe(null, PageFallback);

        view.Message.ShouldBe(PageFallback);
        view.SecondaryMessage.ShouldBeNull();
        view.TechnicalDetail.ShouldBeNull();
        view.UnmappedErrorCode.ShouldBeNull();
    }

    [Fact]
    public void Describe_ForAFrontendAuthoredProblem_KeepsBothPolishLines()
    {
        // The Frontend writes a short Polish title plus a longer Polish detail; both are user-facing.
        ProblemDetails problem = ApiProblemCopy.FrontendAuthored(
            "Hasła nie są zgodne",
            "Nowe hasło i jego powtórzenie muszą być identyczne.");

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.Message.ShouldBe("Hasła nie są zgodne");
        view.SecondaryMessage.ShouldBe("Nowe hasło i jego powtórzenie muszą być identyczne.");
        view.TechnicalDetail.ShouldBeNull();
    }

    [Fact]
    public void Describe_ForAFrontendAuthoredProblemWithOnlyADetail_PromotesTheDetailToTheHeadline()
    {
        ProblemDetails problem = ApiProblemCopy.FrontendAuthored(
            title: "   ",
            detail: "Serwer nie odpowiedział w wyznaczonym czasie.");

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.Message.ShouldBe("Serwer nie odpowiedział w wyznaczonym czasie.");
        view.SecondaryMessage.ShouldBeNull();
    }

    [Fact]
    public void Describe_ForAFrontendAuthoredProblemWithNoText_UsesTheCallSiteFallback()
    {
        ApiProblemView view = ApiProblemCopy.Describe(ApiProblemCopy.FrontendAuthored("  "), PageFallback);

        view.Message.ShouldBe(PageFallback);
        view.TechnicalDetail.ShouldBeNull();
    }

    [Theory]
    [InlineData("Unauthorized", 401, "Twoja sesja wygasła. Zaloguj się ponownie.")]
    [InlineData("Forbidden", 403, "Nie masz uprawnień do wykonania tej operacji.")]
    [InlineData("Not Found", 404, "Nie znaleziono żądanych danych.")]
    [InlineData("Method Not Allowed", 405, "Ta operacja nie jest dostępna dla tego zasobu.")]
    [InlineData("Unsupported Media Type", 415, "Serwer nie przyjmuje pliku w tym formacie.")]
    public void Describe_ForAStatusCodePagesProblemWithNoErrorCode_TranslatesInsteadOfPassingTheEnglishThrough(
        string apiReasonPhrase,
        int status,
        string expectedMessage)
    {
        // Both APIs run AddProblemDetails() + UseStatusCodePages(), which writes a bare English reason
        // phrase and NO errorCode — see the committed ProblemDetailsSnapshotTests contracts. Treating
        // "no code" as "the Frontend wrote it, so it is already Polish" would paint "Unauthorized"
        // onto the page, which is the very defect #548 reports.
        ProblemDetails problem = new() { Title = apiReasonPhrase, Status = status };

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.Message.ShouldBe(expectedMessage);
        view.Message.ShouldNotContain(apiReasonPhrase);
        view.TechnicalDetail.ShouldBe(apiReasonPhrase);
    }

    [Fact]
    public void Describe_WhenAnApiBodyClaimsToBeFrontendAuthored_StillTranslatesIt()
    {
        // The marker is ours alone; ParseProblemDetails strips it off anything parsed from the wire,
        // so a body carrying that member cannot smuggle its English past the lookup.
        ProblemDetails wireProblem = new()
        {
            Title = "Forbidden",
            Status = 403,
            Extensions = { [ApiProblemCopy.FrontendAuthoredExtensionKey] = true }
        };

        ApiProblemCopy.StripFrontendAuthoredMarker(wireProblem);
        ApiProblemView view = ApiProblemCopy.Describe(wireProblem, PageFallback);

        view.Message.ShouldBe("Nie masz uprawnień do wykonania tej operacji.");
    }

    [Fact]
    public async Task ParseProblemDetails_ForABodyClaimingTheFrontendMarker_StripsItAtTheHttpSeam()
    {
        HttpClient httpClient = new(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.Forbidden,
            $$"""{ "title": "Forbidden", "status": 403, "{{ApiProblemCopy.FrontendAuthoredExtensionKey}}": true }"""))
        {
            BaseAddress = new Uri("https://localhost:5002/")
        };

        ApiResult result = await httpClient.DeleteApiResultAsync("api/v1/game-versions/1");
        ApiProblemView view = ApiProblemCopy.Describe(result.ProblemDetails, PageFallback);

        view.Message.ShouldBe("Nie masz uprawnień do wykonania tej operacji.");
    }

    [Theory]
    [InlineData("GameVersionEntity.LotroNotationVersion.AlreadyTaken", 422, "Taka wersja gry jest już zarejestrowana.")]
    [InlineData("GameVersionEntity.LotroNotationVersion.InvalidFormat", 400, "Numer wersji gry musi składać się z liczb rozdzielonych kropkami, na przykład „48.0” albo „47.1.1”.")]
    [InlineData("GameVersionEntity.CannotDeleteReferencedVersion", 422, "Tej wersji gry nie można usunąć, bo są z nią powiązane tłumaczenia.")]
    [InlineData("GameVersionEntity.ProcessedCannotBeDeleted", 422, "Tej wersji gry nie można usunąć, bo zaimportowano już do niej teksty.")]
    [InlineData("TranslationEntity.CannotApproveWithoutTranslation", 422, "Nie można zatwierdzić pustego tłumaczenia — najpierw wpisz polski tekst.")]
    [InlineData("Translations.Validation", 400, "Tłumaczenie nie może być puste i nie może przekraczać dozwolonej długości.")]
    [InlineData("Auth.InvalidCurrentPassword", 400, "Aktualne hasło jest nieprawidłowe.")]
    [InlineData("Validation.FluentValidation", 400, "Formularz zawiera nieprawidłowe dane.")]
    [InlineData("Internal.UnhandledException", 500, "Wystąpił nieoczekiwany błąd serwera. Spróbuj ponownie za chwilę.")]
    public void Describe_WhenTheErrorCodeIsMapped_ShowsThePolishCopy(
        string errorCode,
        int status,
        string expectedMessage)
    {
        ProblemDetails problem = ApiProblem(errorCode, status, "Validation Error", "The English wording.");

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.Message.ShouldBe(expectedMessage);
        view.UnmappedErrorCode.ShouldBeNull();
    }

    [Fact]
    public void Describe_WhenTheErrorCodeIsMapped_KeepsTheApiWordingAsTechnicalDetailOnly()
    {
        // The numbers an admin needs (line numbers, row counts) live in the API's own message, so it
        // is kept — one click away, never as the message itself.
        ProblemDetails problem = ApiProblem(
            "Import.MassRemovalBlocked",
            422,
            "Data Conflict",
            "The upload would remove 812 of 1000 active row(s) (81%), exceeding the 30% safety threshold.");

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.Message.ShouldNotContain("The upload would remove");
        view.TechnicalDetail.ShouldBe(
            "Import.MassRemovalBlocked — The upload would remove 812 of 1000 active row(s) (81%), "
            + "exceeding the 30% safety threshold.");
    }

    [Fact]
    public void Describe_WhenTheApiSendsNoDetail_FallsBackToTheApiTitleForTheTechnicalDetail()
    {
        ProblemDetails problem = ApiProblem("Internal.UnhandledException", 500, "Internal Server Error", detail: null);

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.TechnicalDetail.ShouldBe("Internal.UnhandledException — Internal Server Error");
    }

    [Fact]
    public void Describe_WhenTheApiSendsNeitherTitleNorDetail_UsesTheBareCodeAsTechnicalDetail()
    {
        ProblemDetails problem = ApiProblem("Internal.UnhandledException", 500, title: null, detail: null);

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.TechnicalDetail.ShouldBe("Internal.UnhandledException");
    }

    [Theory]
    [InlineData(400, "Przesłane dane są nieprawidłowe.")]
    [InlineData(401, "Twoja sesja wygasła. Zaloguj się ponownie.")]
    [InlineData(403, "Nie masz uprawnień do wykonania tej operacji.")]
    [InlineData(404, "Nie znaleziono żądanych danych.")]
    [InlineData(422, "Operacja jest w konflikcie z aktualnym stanem danych.")]
    [InlineData(500, "Wystąpił nieoczekiwany błąd serwera. Spróbuj ponownie za chwilę.")]
    public void Describe_WhenTheErrorCodeIsUnmapped_DegradesToThePolishStatusCopyAndReportsTheCode(
        int status,
        string expectedMessage)
    {
        ProblemDetails problem = ApiProblem("Catalog.SomethingNobodyMappedYet", status, "Validation Error", "English.");

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.Message.ShouldBe(expectedMessage);
        view.UnmappedErrorCode.ShouldBe("Catalog.SomethingNobodyMappedYet");
        view.TechnicalDetail.ShouldBe("Catalog.SomethingNobodyMappedYet — English.");
    }

    [Fact]
    public void Describe_WhenTheErrorCodeAndTheStatusAreBothUnmapped_UsesTheCallSiteFallback()
    {
        ProblemDetails problem = ApiProblem("Catalog.SomethingNobodyMappedYet", 418, "Error", "English.");

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.Message.ShouldBe(PageFallback);
        view.UnmappedErrorCode.ShouldBe("Catalog.SomethingNobodyMappedYet");
    }

    [Fact]
    public void Describe_WhenTheErrorCodeIsUnmappedAndTheProblemCarriesNoStatus_UsesTheCallSiteFallback()
    {
        ProblemDetails problem = new()
        {
            Title = "Validation Error",
            Extensions = { [ApiProblemCopy.ErrorCodeExtensionKey] = "Catalog.SomethingNobodyMappedYet" }
        };

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.Message.ShouldBe(PageFallback);
        view.UnmappedErrorCode.ShouldBe("Catalog.SomethingNobodyMappedYet");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Describe_WhenTheErrorCodeIsBlank_DegradesByStatusRatherThanShowingTheEnglish(string blankErrorCode)
    {
        ProblemDetails problem = new()
        {
            Title = "Service Unavailable",
            Status = 503,
            Extensions = { [ApiProblemCopy.ErrorCodeExtensionKey] = blankErrorCode }
        };

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.Message.ShouldBe("Usługa jest chwilowo niedostępna. Spróbuj ponownie za chwilę.");
        view.UnmappedErrorCode.ShouldBeNull();
    }

    [Fact]
    public void Describe_WhenTheErrorCodeIsNotAString_DegradesByStatusRatherThanShowingTheEnglish()
    {
        ProblemDetails problem = new()
        {
            Title = "Service Unavailable",
            Status = 503,
            Extensions = { [ApiProblemCopy.ErrorCodeExtensionKey] = 42 }
        };

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.Message.ShouldBe("Usługa jest chwilowo niedostępna. Spróbuj ponownie za chwilę.");
        view.UnmappedErrorCode.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Describe_WhenTheCallSiteFallbackIsBlank_FloorsOnTheGenericMessage(string blankFallback)
    {
        // Every branch shares the same floor, so no call site can render an empty red box.
        ApiProblemCopy.Describe(null, blankFallback).Message.ShouldBe(ApiProblemCopy.GenericMessage);
        ApiProblemCopy.Describe(ApiProblemCopy.FrontendAuthored("  "), blankFallback).Message
            .ShouldBe(ApiProblemCopy.GenericMessage);
        ApiProblemCopy.Describe(ApiProblem("Catalog.Unmapped", 418, "Error", "English."), blankFallback).Message
            .ShouldBe(ApiProblemCopy.GenericMessage);
    }

    [Fact]
    public void Describe_WhenTheErrorCodeArrivesAsAJsonElement_StillResolvesTheCopy()
    {
        // The wire shape: deserializing ProblemDetails puts unknown members into Extensions as
        // JsonElement, not string. Reading only `is string` would silently miss every real response.
        JsonElement wireErrorCode = JsonSerializer.Deserialize<JsonElement>("\"Auth.UserAlreadyExistsByEmail\"");
        ProblemDetails problem = new()
        {
            Title = "Data Conflict",
            Detail = "A user with this email address already exists.",
            Status = 422,
            Extensions = { [ApiProblemCopy.ErrorCodeExtensionKey] = wireErrorCode }
        };

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.Message.ShouldBe("Konto z tym adresem e-mail już istnieje.");
        view.UnmappedErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task Describe_OnAProblemParsedFromARealApiResponse_ShowsThePolishCopy()
    {
        // End to end across the seam that actually produces the ProblemDetails a page renders: the
        // body below is exactly what ErrorExtensions.ToProblemDetails writes for a rejected save.
        const string apiResponseBody = """
            {
              "type": "https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/400",
              "title": "Validation Error",
              "status": 400,
              "detail": "'Translated Text' must not be empty.",
              "errorCode": "Translations.Validation"
            }
            """;
        HttpClient httpClient = new(StubHttpMessageHandler.RespondWith(HttpStatusCode.BadRequest, apiResponseBody))
        {
            BaseAddress = new Uri("https://localhost:5002/")
        };

        ApiResult result = await httpClient.PutApiResultAsync("api/v1/translations/620756992/1001", new { });
        ApiProblemView view = ApiProblemCopy.Describe(result.ProblemDetails, "Nie udało się zapisać tłumaczenia.");

        result.IsFailure.ShouldBeTrue();
        view.Message.ShouldBe("Tłumaczenie nie może być puste i nie może przekraczać dozwolonej długości.");
        view.TechnicalDetail.ShouldBe("Translations.Validation — 'Translated Text' must not be empty.");
    }

    [Fact]
    public void Localize_ForAnUnmappedErrorCode_LogsTheGapAndKeepsTheTraceIdAndType()
    {
        // The download routes have no collapsible block and no component, so the warning and the
        // correlation token have to survive here or the gap is silent (ADR-0044 §3).
        ProblemDetails problem = ApiProblem("Catalog.SomethingNobodyMappedYet", 403, "Forbidden", "Not your row.");
        problem.Type = "https://tools.ietf.org/html/rfc9110#section-15.5.4";
        problem.Extensions[ApiProblemCopy.TraceIdExtensionKey] = "00-abc-def-01";
        CapturingLoggerProvider loggerProvider = new();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));

        ProblemDetails localized = ApiProblemCopy.Localize(loggerFactory, problem, PageFallback, 502);

        localized.Title.ShouldBe("Nie masz uprawnień do wykonania tej operacji.");
        localized.Status.ShouldBe(403);
        localized.Type.ShouldBe("https://tools.ietf.org/html/rfc9110#section-15.5.4");
        localized.Extensions[ApiProblemCopy.TraceIdExtensionKey].ShouldBe("00-abc-def-01");
        loggerProvider.Entries.ShouldContain(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("Catalog.SomethingNobodyMappedYet"));
    }

    [Fact]
    public void Localize_ForAMappedErrorCode_LogsNothing()
    {
        CapturingLoggerProvider loggerProvider = new();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));

        ApiProblemCopy.Localize(
            loggerFactory,
            ApiProblem("TranslationFiles.NotFound", 404, "Not Found", "No translation file yet."),
            PageFallback,
            502);

        loggerProvider.Entries.ShouldBeEmpty();
    }

    private static ProblemDetails ApiProblem(string errorCode, int status, string? title, string? detail)
        => new()
        {
            Title = title,
            Detail = detail,
            Status = status,
            Extensions = { [ApiProblemCopy.ErrorCodeExtensionKey] = errorCode }
        };
}
