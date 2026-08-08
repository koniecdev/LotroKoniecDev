using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace LotroKoniecDev.Frontend.Infrastructure.Errors;

/// <summary>
/// The single source of Polish copy for a failed API call (ADR-0044). The APIs write English —
/// a domain message keyed by a machine-readable <c>errorCode</c>, or, for a status the
/// <c>UseStatusCodePages</c> middleware synthesizes, a bare English reason phrase with no code at
/// all. So the default is to translate, and only a <see cref="ProblemDetails"/> the Frontend
/// authored itself (via <see cref="FrontendAuthored"/>) is rendered as-is.
/// <para>
/// The default runs that way round deliberately: an unrecognised problem degrades to Polish, so a
/// surface nobody anticipated cannot leak English. Getting the marker wrong shows a generic Polish
/// sentence instead of a specific one — never the bug this exists to fix.
/// </para>
/// </summary>
internal static class ApiProblemCopy
{
    internal const string ErrorCodeExtensionKey = "errorCode";

    /// <summary>
    /// Marks a <see cref="ProblemDetails"/> the Frontend wrote itself — already Polish, already
    /// user-facing, nothing to translate. Stamped only by <see cref="FrontendAuthored"/> and
    /// stripped from anything parsed off the wire, so an API body can never claim it.
    /// </summary>
    internal const string FrontendAuthoredExtensionKey = "frontendAuthored";

    /// <summary>
    /// Where <see cref="Localize"/> parks the API's own wording. A raw problem body has no
    /// collapsible block to hide it in, so it moves out of <c>Detail</c> instead of being dropped.
    /// </summary>
    internal const string TechnicalDetailExtensionKey = "technicalDetail";

    /// <summary>The correlation token both APIs stamp on every problem body.</summary>
    internal const string TraceIdExtensionKey = "traceId";

    /// <summary>
    /// The last resort: an API-authored failure whose code is unmapped and whose status carries no
    /// copy either. Reaching it is a gap in <see cref="PolishByErrorCode"/>, and
    /// <c>ApiProblemAlert</c> logs the code when it does.
    /// </summary>
    internal const string GenericMessage = "Operacja nie powiodła się. Spróbuj ponownie za chwilę.";

    /// <summary>
    /// Polish copy per API error code. Covers every code <c>TranslationSystem.API</c> and
    /// <c>AuthSystem.API</c> can produce — the domain error catalogues, the per-slice validation
    /// errors and the shared exception handlers. A code missing here degrades to
    /// <see cref="PolishByStatusCode"/>, never to the API's English.
    /// </summary>
    private static readonly FrozenDictionary<string, string> PolishByErrorCode =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ── TMS · game versions ────────────────────────────────────────────────────────────
            ["GameVersionEntity.NotFound"] =
                "Nie znaleziono wskazanej wersji gry.",
            ["GameVersionEntity.LotroNotationVersion.AlreadyTaken"] =
                "Taka wersja gry jest już zarejestrowana.",
            ["GameVersionEntity.LotroNotationVersion.NullOrEmpty"] =
                "Podaj numer wersji gry.",
            ["GameVersionEntity.LotroNotationVersion.LongerThanAllowed"] =
                "Numer wersji gry jest za długi.",
            ["GameVersionEntity.LotroNotationVersion.InvalidFormat"] =
                "Numer wersji gry musi składać się z liczb rozdzielonych kropkami, na przykład „48.0” albo „47.1.1”.",
            ["GameVersionEntity.SupersededCannotBeProcessed"] =
                "Ta wersja gry została zastąpiona nowszą i nie może już zostać przetworzona.",
            ["GameVersionEntity.ProcessedCannotBeSuperseded"] =
                "Ta wersja gry jest już przetworzona i nie można oznaczyć jej jako zastąpionej.",
            ["GameVersionEntity.ProcessedCannotBeDeleted"] =
                "Tej wersji gry nie można usunąć, bo zaimportowano już do niej teksty.",
            ["GameVersionEntity.CannotDeleteReferencedVersion"] =
                "Tej wersji gry nie można usunąć, bo są z nią powiązane tłumaczenia.",
            ["GameVersions.Validation"] =
                "Podane dane wersji gry są nieprawidłowe.",

            // ── TMS · translations ─────────────────────────────────────────────────────────────
            ["TranslationEntity.NotFound"] =
                "Nie znaleziono tego tłumaczenia.",
            ["TranslationEntity.CannotEditRemoved"] =
                "Tego tłumaczenia nie można edytować — odpowiadający mu tekst został usunięty z gry.",
            ["TranslationEntity.CannotApproveWithoutTranslation"] =
                "Nie można zatwierdzić pustego tłumaczenia — najpierw wpisz polski tekst.",
            ["TranslationEntity.CannotApproveRemoved"] =
                "Tego tłumaczenia nie można zatwierdzić — odpowiadający mu tekst został usunięty z gry.",
            ["TranslationEntity.FileId.Invalid"] =
                "Nieprawidłowy identyfikator pliku.",
            ["TranslationEntity.GossipId.Invalid"] =
                "Nieprawidłowy identyfikator fragmentu.",
            ["Translations.Validation"] =
                "Tłumaczenie nie może być puste i nie może przekraczać dozwolonej długości.",
            ["Translations.UnsupportedLanguage"] =
                "Ten język nie jest obsługiwany — dostępny jest wyłącznie polski.",

            // ── TMS · translation file ─────────────────────────────────────────────────────────
            ["TranslationFiles.NotFound"] =
                "Plik z tłumaczeniami nie został jeszcze zbudowany. Zatwierdź przynajmniej jedno tłumaczenie i spróbuj ponownie.",
            ["TranslationFiles.UnsupportedLanguage"] =
                "Ten język nie jest obsługiwany — dostępny jest wyłącznie polski.",

            // ── TMS · import ───────────────────────────────────────────────────────────────────
            ["Import.Validation"] =
                "Podane dane importu są nieprawidłowe.",
            ["Import.ParseFailed"] =
                "Plik zawiera wiersze, których nie udało się odczytać. Import został odrzucony w całości, "
                + "żeby uszkodzony plik nie skasował istniejących tłumaczeń.",
            ["Import.InvalidRow"] =
                "Plik zawiera nieprawidłowy wiersz.",
            ["Import.DuplicateFragmentKey"] =
                "Plik zawiera ten sam fragment więcej niż raz.",
            ["Import.EmptyUpload"] =
                "Plik nie zawiera żadnych tekstów do zaimportowania.",
            ["Import.MassRemovalBlocked"] =
                "Import usunąłby zbyt dużą część aktywnych tekstów i został wstrzymany. "
                + "Jeśli to zamierzone, powtórz import z zaznaczoną opcją wymuszenia.",

            // ── TMS · translator profile ───────────────────────────────────────────────────────
            ["TranslatorEntity.NotFound"] =
                "Nie znaleziono profilu tłumacza.",
            ["TranslatorEntity.DisplayName.NullOrEmpty"] =
                "Podaj nazwę wyświetlaną.",
            ["TranslatorEntity.DisplayName.LongerThanAllowed"] =
                "Nazwa wyświetlana jest za długa.",
            ["TranslatorEntity.Email.LongerThanAllowed"] =
                "Adres e-mail jest za długi.",
            ["TranslatorEntity.Email.InvalidFormat"] =
                "Adres e-mail jest nieprawidłowy.",
            ["Translators.Unauthenticated"] =
                "Twoja sesja wygasła. Zaloguj się ponownie.",

            // ── Auth · accounts ────────────────────────────────────────────────────────────────
            ["Auth.UserAlreadyExistsByEmail"] =
                "Konto z tym adresem e-mail już istnieje.",
            ["Auth.UserAlreadyExistsByUsername"] =
                "Konto z tą nazwą użytkownika już istnieje.",
            ["Auth.RegistrationFailed"] =
                "Nie udało się założyć konta. Sprawdź podane dane i spróbuj ponownie.",
            ["Auth.UserNotFound"] =
                "Nie znaleziono konta.",
            ["Auth.InvalidCurrentPassword"] =
                "Aktualne hasło jest nieprawidłowe.",
            ["Auth.PasswordChangeFailed"] =
                "Nie udało się zmienić hasła. Nowe hasło może nie spełniać wymagań.",
            ["Auth.InvalidPasswordResetToken"] =
                "Link do zmiany hasła jest nieprawidłowy lub wygasł. Poproś o nowy.",
            ["Auth.PasswordResetFailed"] =
                "Nie udało się ustawić nowego hasła. Może ono nie spełniać wymagań.",
            ["Auth.InvalidEmailConfirmationToken"] =
                "Link potwierdzający adres e-mail jest nieprawidłowy lub wygasł. Poproś o nowy.",
            ["Auth.EmailConfirmationFailed"] =
                "Nie udało się potwierdzić adresu e-mail.",
            ["Auth.DeletionAlreadyScheduled"] =
                "Usunięcie konta jest już zaplanowane.",
            ["Auth.DeletionSchedulingFailed"] =
                "Nie udało się zaplanować usunięcia konta. Twoje konto pozostaje bez zmian — spróbuj ponownie później.",
            ["Auth.AccountDeletionFailed"] =
                "Nie udało się usunąć konta. Spróbuj ponownie później.",
            ["Auth.InvalidCancelDeletionToken"] =
                "Link anulujący usunięcie konta jest nieprawidłowy lub wygasł.",
            ["Auth.CancelDeletionFailed"] =
                "Nie udało się anulować usunięcia konta. Spróbuj ponownie później.",

            // ── Auth · per-slice request validation ────────────────────────────────────────────
            ["RegisterUser.Validation"] =
                "Formularz rejestracji zawiera nieprawidłowe dane.",
            ["ChangePassword.Validation"] =
                "Formularz zmiany hasła zawiera nieprawidłowe dane.",
            ["ForgotPassword.Validation"] =
                "Podany adres e-mail jest nieprawidłowy.",
            ["ResetPassword.Validation"] =
                "Formularz zmiany hasła zawiera nieprawidłowe dane.",
            ["ConfirmEmail.Validation"] =
                "Link potwierdzający adres e-mail jest niekompletny lub nieprawidłowy.",
            ["ResendEmailConfirmation.Validation"] =
                "Podany adres e-mail jest nieprawidłowy.",
            ["DeleteAccount.Validation"] =
                "Formularz usunięcia konta zawiera nieprawidłowe dane.",
            ["CancelAccountDeletion.Validation"] =
                "Link anulujący usunięcie konta jest niekompletny lub nieprawidłowy.",

            // ── Shared exception handlers (both APIs) ──────────────────────────────────────────
            ["Validation.FluentValidation"] =
                "Formularz zawiera nieprawidłowe dane.",
            ["Db.ConcurrencyConflict"] =
                "Dane zostały w międzyczasie zmienione przez kogoś innego. Odśwież stronę i spróbuj ponownie.",
            ["Http.BadRequest"] =
                "Serwer nie przyjął żądania — dane są nieprawidłowe lub niekompletne.",
            ["Http.PayloadTooLarge"] =
                "Wysyłany plik jest za duży.",
            ["Http.InvalidArgument"] =
                "Żądanie zawiera nieprawidłowe dane.",
            ["Internal.UnhandledException"] =
                "Wystąpił nieoczekiwany błąd serwera. Spróbuj ponownie za chwilę."
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The degradation path for an API-authored failure whose code has no copy yet: say what the
    /// status means in Polish rather than fall back to the API's English. Deliberately outranks the
    /// call site's own fallback, which only says that something failed.
    /// </summary>
    private static readonly FrozenDictionary<int, string> PolishByStatusCode =
        new Dictionary<int, string>
        {
            [StatusCodes.Status400BadRequest] = "Przesłane dane są nieprawidłowe.",
            [StatusCodes.Status401Unauthorized] = "Twoja sesja wygasła. Zaloguj się ponownie.",
            [StatusCodes.Status403Forbidden] = "Nie masz uprawnień do wykonania tej operacji.",
            [StatusCodes.Status404NotFound] = "Nie znaleziono żądanych danych.",
            [StatusCodes.Status405MethodNotAllowed] = "Ta operacja nie jest dostępna dla tego zasobu.",
            [StatusCodes.Status408RequestTimeout] = "Serwer nie doczekał się całego żądania. Spróbuj ponownie.",
            [StatusCodes.Status409Conflict] = "Operacja jest w konflikcie z aktualnym stanem danych.",
            [StatusCodes.Status415UnsupportedMediaType] = "Serwer nie przyjmuje pliku w tym formacie.",
            [StatusCodes.Status413PayloadTooLarge] = "Wysyłany plik jest za duży.",
            [StatusCodes.Status422UnprocessableEntity] = "Operacja jest w konflikcie z aktualnym stanem danych.",
            [StatusCodes.Status429TooManyRequests] = "Zbyt wiele żądań. Odczekaj chwilę i spróbuj ponownie.",
            [StatusCodes.Status500InternalServerError] = "Wystąpił nieoczekiwany błąd serwera. Spróbuj ponownie za chwilę.",
            [StatusCodes.Status502BadGateway] = "Usługa jest chwilowo niedostępna. Spróbuj ponownie za chwilę.",
            [StatusCodes.Status503ServiceUnavailable] = "Usługa jest chwilowo niedostępna. Spróbuj ponownie za chwilę.",
            [StatusCodes.Status504GatewayTimeout] = "Serwer nie odpowiedział w wyznaczonym czasie. Spróbuj ponownie."
        }.ToFrozenDictionary();

    /// <summary>
    /// Turns a failure into what the page shows. <paramref name="fallbackMessage"/> is the page's own
    /// contextual Polish sentence, used when there is no problem object at all or when a
    /// Frontend-authored problem carries no text.
    /// </summary>
    public static ApiProblemView Describe(ProblemDetails? problem, string fallbackMessage)
    {
        string fallback = FirstNonBlank(fallbackMessage) ?? GenericMessage;

        if (problem is null)
        {
            return new ApiProblemView(fallback, null, null, null);
        }

        if (IsFrontendAuthored(problem))
        {
            // Already Polish. Its title and detail are both user-facing, so neither is dropped.
            return FirstNonBlank(problem.Title) is { } polishTitle
                ? new ApiProblemView(polishTitle, FirstNonBlank(problem.Detail), null, null)
                : new ApiProblemView(FirstNonBlank(problem.Detail) ?? fallback, null, null, null);
        }

        string? errorCode = ReadErrorCode(problem);
        string? technicalDetail = BuildTechnicalDetail(errorCode, problem);

        if (errorCode is not null && PolishByErrorCode.TryGetValue(errorCode, out string? mapped))
        {
            return new ApiProblemView(mapped, null, technicalDetail, null);
        }

        // No code at all is the UseStatusCodePages shape — a bare English reason phrase like
        // "Unauthorized". It degrades exactly like an unmapped code, and is just as untranslatable
        // by hand, so the status copy answers both.
        string degraded = problem.Status is { } status && PolishByStatusCode.TryGetValue(status, out string? byStatus)
            ? byStatus
            : fallback;

        return new ApiProblemView(degraded, null, technicalDetail, errorCode);
    }

    /// <summary>
    /// A problem the Frontend wrote for its own reasons — a Polly transport failure, a rel the
    /// server does not advertise, an inline form guard. Polish by construction, never translated.
    /// </summary>
    public static ProblemDetails FrontendAuthored(string title, string? detail = null, int? status = null)
        => new()
        {
            Title = title,
            Detail = detail,
            Status = status,
            Extensions = { [FrontendAuthoredExtensionKey] = true }
        };

    /// <summary>
    /// Removes the Frontend-authored marker from a problem parsed off the wire, so an API response
    /// carrying that member (ours never does) cannot pass its English through untranslated.
    /// </summary>
    public static void StripFrontendAuthoredMarker(ProblemDetails problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        problem.Extensions.Remove(FrontendAuthoredExtensionKey);
    }

    private static bool IsFrontendAuthored(ProblemDetails problem)
        => problem.Extensions.TryGetValue(FrontendAuthoredExtensionKey, out object? marker)
           && marker is true;

    /// <summary>
    /// The same translation for a route that answers with a raw problem body instead of a rendered
    /// page — the file-download endpoints, whose failure the browser shows verbatim.
    /// </summary>
    public static ProblemDetails Localize(
        ILoggerFactory loggerFactory,
        ProblemDetails? problem,
        string fallbackMessage,
        int fallbackStatus)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        ApiProblemView view = Describe(problem, fallbackMessage);
        ReportUnmappedErrorCode(loggerFactory.CreateLogger(nameof(ApiProblemCopy)), view, problem?.Status);

        ProblemDetails localized = new()
        {
            Title = view.Message,
            Detail = view.SecondaryMessage,
            Status = problem?.Status ?? fallbackStatus,
            Type = problem?.Type
        };

        if (view.TechnicalDetail is { } technicalDetail)
        {
            localized.Extensions[TechnicalDetailExtensionKey] = technicalDetail;
        }

        // The trace id is the one token that ties a support report to the server-side log; it is the
        // API's, not ours to mint, so it rides across rather than being rebuilt away.
        if (problem?.Extensions.TryGetValue(TraceIdExtensionKey, out object? traceId) is true)
        {
            localized.Extensions[TraceIdExtensionKey] = traceId;
        }

        return localized;
    }

    /// <summary>
    /// Reports an API error code that shipped before its Polish copy. Shared by both renderers so
    /// the gap is never silent, whichever surface degraded (ADR-0044 §3).
    /// </summary>
    public static void ReportUnmappedErrorCode(ILogger logger, ApiProblemView view, int? status)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (view.UnmappedErrorCode is { } unmappedErrorCode)
        {
            LogUnmappedErrorCode(logger, unmappedErrorCode, status, null);
        }
    }

    private static readonly Action<ILogger, string, int?, Exception?> LogUnmappedErrorCode =
        LoggerMessage.Define<string, int?>(
            LogLevel.Warning,
            new EventId(1, nameof(LogUnmappedErrorCode)),
            "API error code {ErrorCode} (status {StatusCode}) has no Polish copy; showed the status fallback.");

    /// <summary>
    /// The <c>errorCode</c> arrives as a <see cref="JsonElement"/> when the problem was deserialized
    /// from the wire and as a plain <see cref="string"/> when it was constructed in-process.
    /// </summary>
    private static string? ReadErrorCode(ProblemDetails problem)
    {
        if (!problem.Extensions.TryGetValue(ErrorCodeExtensionKey, out object? rawErrorCode))
        {
            return null;
        }

        string? errorCode = rawErrorCode switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        };

        return string.IsNullOrWhiteSpace(errorCode) ? null : errorCode;
    }

    /// <summary>
    /// The code and the API's own wording, whichever of the two the problem actually carries.
    /// A problem with neither (a body-less status) has nothing worth showing.
    /// </summary>
    private static string? BuildTechnicalDetail(string? errorCode, ProblemDetails problem)
        => (errorCode, FirstNonBlank(problem.Detail, problem.Title)) switch
        {
            (not null, { } apiMessage) => $"{errorCode} — {apiMessage}",
            (not null, null) => errorCode,
            (null, { } apiMessage) => apiMessage,
            _ => null
        };

    private static string? FirstNonBlank(params string?[] candidates)
        => Array.Find(candidates, candidate => !string.IsNullOrWhiteSpace(candidate));
}
