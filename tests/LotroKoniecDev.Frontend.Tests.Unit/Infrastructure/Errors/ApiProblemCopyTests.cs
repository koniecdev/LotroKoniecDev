using System.Net;
using System.Text.Json;
using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Tests.Unit.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Errors;

/// <summary>
/// The lookup that turns an API failure into Polish text (#548, ADR-0044). The rule under test: a
/// problem from an API is looked up by its <c>errorCode</c> and its English wording never becomes the
/// message, while a problem the Frontend wrote, which has no code and is already Polish, passes through
/// unchanged.
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
        // Both APIs use AddProblemDetails() and UseStatusCodePages(), which write a bare English phrase
        // and no errorCode. See the committed ProblemDetailsSnapshotTests. Reading "no code" as "the
        // Frontend wrote this, so it is already Polish" would print "Unauthorized" onto the page, which
        // is exactly the bug #548 reports.
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

    [Theory]
    [InlineData("Import.ParseFailed", "The upload has 3 unparseable line(s); first failure — line 42: unexpected pipe.")]
    [InlineData("Import.InvalidRow", "Row (620756992, 1001) is invalid: args_order must be NULL or dash-separated positions.")]
    [InlineData("Import.DuplicateFragmentKey", "The upload contains more than one row for fragment (620756992, 1001).")]
    [InlineData("Import.MassRemovalBlocked", "The upload would remove 812 of 1000 active row(s) (81%), exceeding the 30% safety threshold.")]
    public void Describe_WhenTheMappedCodesApiMessageCarriesData_KeepsItAsTechnicalDetailOnly(
        string errorCode,
        string apiMessage)
    {
        // The numbers an admin needs, such as line numbers, row keys and removal counts, live only in the
        // API's own message, so we keep it one click away and never use it as the message itself. These
        // four codes are the whole reason ADR-0044 §4 exists.
        ProblemDetails problem = ApiProblem(errorCode, 422, "Data Conflict", apiMessage);

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.Message.ShouldNotContain(apiMessage);
        view.TechnicalDetail.ShouldBe($"{errorCode} — {apiMessage}");
        // Without this the theory would also pass if the code fell off PolishByErrorCode, because an
        // unmapped code keeps its detail too. The set is only meaningful for codes that are mapped.
        view.UnmappedErrorCode.ShouldBeNull();
    }

    [Theory]
    [InlineData("Auth.InvalidCurrentPassword", 400, "The current password is incorrect.")]
    [InlineData("Translations.Validation", 400, "'Translated Text' must not be empty.")]
    [InlineData("GameVersionEntity.LotroNotationVersion.AlreadyTaken", 422, "The lotronotationversion value '48.0' is already taken.")]
    [InlineData("Import.EmptyUpload", 422, "The upload contains no translatable rows.")]
    [InlineData("Import.Validation", 400, "'File' must not be empty.")]
    [InlineData("Internal.UnhandledException", 500, "An unexpected error occurred.")]
    public void Describe_WhenTheMappedCodesApiMessageOnlyRestatesThePolish_HidesTheTechnicalDetail(
        string errorCode,
        int status,
        string apiMessage)
    {
        // #703: the drawer repeated the headline in English and read like a leak. The Polish sentence is
        // the whole message unless the API's wording carries data it cannot reproduce.
        ProblemDetails problem = ApiProblem(errorCode, status, "Validation Error", apiMessage);

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.TechnicalDetail.ShouldBeNull();
    }

    [Fact]
    public void Describe_WhenADataCarryingCodeSendsNoDetail_FallsBackToTheApiTitleForTheTechnicalDetail()
    {
        ProblemDetails problem = ApiProblem("Import.ParseFailed", 422, "Data Conflict", detail: null);

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.TechnicalDetail.ShouldBe("Import.ParseFailed — Data Conflict");
    }

    [Fact]
    public void Describe_WhenADataCarryingCodeSendsNeitherTitleNorDetail_UsesTheBareCodeAsTechnicalDetail()
    {
        ProblemDetails problem = ApiProblem("Import.ParseFailed", 422, title: null, detail: null);

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.TechnicalDetail.ShouldBe("Import.ParseFailed");
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
    [InlineData(500, "Wystąpił nieoczekiwany błąd serwera. Spróbuj ponownie za chwilę.")]
    [InlineData(502, "Usługa jest chwilowo niedostępna. Spróbuj ponownie za chwilę.")]
    [InlineData(503, "Usługa jest chwilowo niedostępna. Spróbuj ponownie za chwilę.")]
    [InlineData(504, "Serwer nie odpowiedział w wyznaczonym czasie. Spróbuj ponownie.")]
    [InlineData(401, "Twoja sesja wygasła. Zaloguj się ponownie.")]
    public void Describe_ForAStatusOnlyProblem_ShowsTheStatusCopy(int status, string expectedCopy)
    {
        // What a stopped upstream produces: the proxy's non-JSON body leaves nothing to translate,
        // so the status ladder is the whole answer (#637).
        ApiProblemView view = ApiProblemCopy.Describe(ApiProblemCopy.StatusOnly(status), PageFallback);

        view.Message.ShouldBe(expectedCopy);
        view.TechnicalDetail.ShouldBeNull();
        view.SecondaryMessage.ShouldBeNull();
    }

    [Theory]
    [InlineData(501)]
    [InlineData(418)]
    public void Describe_ForAStatusOnlyProblemWhoseStatusHasNoCopy_FallsBackToTheCallSiteSentence(int status)
    {
        ApiProblemCopy.Describe(ApiProblemCopy.StatusOnly(status), PageFallback).Message.ShouldBe(PageFallback);
    }

    [Fact]
    public void Describe_ForAnEmptyProblemBodyOffTheWire_ConvergesOnTheSameCopyAsTheStatusOnlySynthesis()
    {
        // "{}" parses, so it takes the other branch of ParseProblemDetails and keeps only the status we
        // filled in. It must read exactly like the case where the body could not be parsed.
        ProblemDetails parsedFromEmptyObject = new() { Status = 502 };

        ApiProblemCopy.Describe(parsedFromEmptyObject, PageFallback).Message
            .ShouldBe(ApiProblemCopy.Describe(ApiProblemCopy.StatusOnly(502), PageFallback).Message);
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
        // End to end through the code that really produces the ProblemDetails a page shows: the
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
        view.TechnicalDetail.ShouldBeNull();
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

    [Fact]
    public void Localize_ForAMappedErrorCode_DropsTheEnglishButKeepsTheTraceId()
    {
        // The download routes answer with a raw body the browser shows as it is. Once #703 removes the
        // English from it, the trace id is the only thing left that links a report to the server log.
        ProblemDetails problem = ApiProblem("TranslationFiles.NotFound", 404, "Not Found", "No translation file yet.");
        problem.Extensions[ApiProblemCopy.TraceIdExtensionKey] = "00-abc-def-01";
        ProblemDetails localized = ApiProblemCopy.Localize(
            NullLoggerFactory.Instance, problem, PageFallback, 502);

        localized.Extensions.ShouldNotContainKey(ApiProblemCopy.TechnicalDetailExtensionKey);
        localized.Extensions[ApiProblemCopy.TraceIdExtensionKey].ShouldBe("00-abc-def-01");
    }

    [Fact]
    public void Describe_WhenTheCodeDiffersOnlyByCase_TreatsItAsUnmappedRatherThanHidingTheEnglish()
    {
        // Both lookups are Ordinal, so a mis-cased code is simply a code we do not know. It must keep
        // its detail: hiding it would leave the user with a generic sentence and nothing to report.
        ProblemDetails problem = ApiProblem("import.parsefailed", 422, "Data Conflict", "Line 42 is bad.");

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.UnmappedErrorCode.ShouldBe("import.parsefailed");
        view.TechnicalDetail.ShouldBe("import.parsefailed — Line 42 is bad.");
    }

    [Fact]
    public void Describe_WhenADataCarryingCodeSendsOnlyBlankText_UsesTheBareCodeAsTechnicalDetail()
    {
        ProblemDetails problem = ApiProblem("Import.ParseFailed", 422, "   ", "  ");

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.TechnicalDetail.ShouldBe("Import.ParseFailed");
    }

    [Theory]
    [InlineData("ChangePassword.Validation", "Password must contain at least one special character.")]
    [InlineData("ChangePassword.Validation", "Password must not exceed 128 characters.")]
    [InlineData("ResetPassword.Validation", "Password must contain at least one special character.")]
    [InlineData("RegisterUser.Validation", "Password must contain at least one special character.")]
    [InlineData("Auth.PasswordChangeFailed", "Passwords must have at least one non alphanumeric character.")]
    [InlineData("Auth.PasswordResetFailed", "Passwords must have at least one digit ('0'-'9').")]
    [InlineData("Auth.RegistrationFailed", "Passwords must have at least one uppercase ('A'-'Z').")]
    public void Describe_ForACodeThatRejectsAPassword_NamesEveryRuleInPolish(string errorCode, string apiMessage)
    {
        // #703 hid the English that used to name the broken rule, so the Polish has to name it instead.
        // Every rule has to be listed, both bounds included: the special character is what the page hint
        // used to omit, and a pasted 129-character passphrase satisfies every other rule on the list.
        ProblemDetails problem = ApiProblem(errorCode, 400, "Validation Error", apiMessage);

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.Message.ShouldContain("od 8 do 128 znaków");
        view.Message.ShouldContain("mała i wielka litera");
        view.Message.ShouldContain("cyfra");
        view.Message.ShouldContain("znak specjalny");
    }

    [Theory]
    [InlineData("Auth.EmailChangeSameAddress", 400, "Podany adres e-mail jest taki sam jak obecny.")]
    [InlineData("Auth.InvalidEmailChangeToken", 400, "Link zmieniający adres e-mail jest nieprawidłowy lub wygasł. Poproś o nowy.")]
    [InlineData("Auth.EmailChangeFailed", 500, "Nie udało się zmienić adresu e-mail. Mógł on zostać w międzyczasie zajęty przez inne konto.")]
    [InlineData("RequestEmailChange.Validation", 400, "Podany adres e-mail jest nieprawidłowy.")]
    public void Describe_ForAnEmailChangeCode_ShowsThePolishCopyInsteadOfDegrading(
        string errorCode,
        int status,
        string expectedMessage)
    {
        // These four shipped with #683 and had no copy, so /account/change-email showed the generic
        // sentence plus an English drawer. Hiding the drawer (#703) makes covering them mandatory.
        ProblemDetails problem = ApiProblem(errorCode, status, "Validation Error", "English.");

        ApiProblemView view = ApiProblemCopy.Describe(problem, PageFallback);

        view.Message.ShouldBe(expectedMessage);
        view.UnmappedErrorCode.ShouldBeNull();
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
