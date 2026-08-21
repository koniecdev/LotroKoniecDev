using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace LotroKoniecDev.Frontend.Infrastructure.Errors;

/// <summary>
/// The one place the Polish text for a failed API call comes from (ADR-0044). The APIs write English:
/// either a domain message with a machine-readable <c>errorCode</c>, or, for a status the
/// <c>UseStatusCodePages</c> middleware produces, a bare English phrase with no code at all.
/// So the default is to translate, and only a <see cref="ProblemDetails"/> the Frontend wrote itself,
/// through <see cref="FrontendAuthored"/>, is shown as it is.
/// <para>
/// The default works that way round on purpose: a problem we do not recognise still comes out in Polish,
/// so a page nobody thought of cannot show English. Getting the marker wrong only shows a general Polish
/// sentence instead of a specific one, and never the bug this exists to prevent.
/// </para>
/// </summary>
internal static class ApiProblemCopy
{
    internal const string ErrorCodeExtensionKey = "errorCode";

    /// <summary>
    /// Marks a <see cref="ProblemDetails"/> the Frontend wrote itself: already Polish, already meant for
    /// the user, nothing to translate. Only <see cref="FrontendAuthored"/> sets it, and it is removed
    /// from anything read off the wire, so an API response can never claim it.
    /// </summary>
    internal const string FrontendAuthoredExtensionKey = "frontendAuthored";

    /// <summary>
    /// Where <see cref="Localize"/> puts the API's own wording. A plain problem body has nowhere to hide
    /// it, so it moves out of <c>Detail</c> instead of being thrown away.
    /// </summary>
    internal const string TechnicalDetailExtensionKey = "technicalDetail";

    /// <summary>The tracking id both APIs put on every problem body.</summary>
    internal const string TraceIdExtensionKey = "traceId";

    /// <summary>
    /// The last resort: a failure from an API whose code has no text here and whose status has none
    /// either. Reaching it means <see cref="PolishByErrorCode"/> is missing an entry, and
    /// <c>ApiProblemAlert</c> logs the code when that happens.
    /// </summary>
    internal const string GenericMessage = "Operacja nie powiodła się. Spróbuj ponownie za chwilę.";

    /// <summary>
    /// The password rules, spelled out. Both layers that reject a password answer with a mapped code, so
    /// since #703 hid the English drawer this sentence is the only place a user learns which rule they
    /// broke. It must list <em>every</em> rule: a rule missing here is a rule the message silently
    /// contradicts, which is the defect #703 exists to remove.
    /// <para>
    /// The wording is shared with the change-password hint, so the Frontend states the rules once. The
    /// API states the same ones in <c>PasswordValidationRules</c> and in the Identity options; the
    /// contexts share no code, so keep those in step by hand.
    /// </para>
    /// </summary>
    internal const string PasswordRules =
        "od 8 do 128 znaków, mała i wielka litera, cyfra oraz znak specjalny.";

    /// <summary>
    /// The Polish text for each API error code. It covers every code <c>TranslationSystem.API</c> and
    /// <c>AuthSystem.API</c> can produce: the domain error lists, the per-slice validation errors and the
    /// shared exception handlers. A code that is missing here falls back to
    /// <see cref="PolishByStatusCode"/> and never to the API's English.
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
                "Nie udało się założyć konta. Sprawdź adres e-mail, nazwę użytkownika oraz wymagania hasła: "
                + PasswordRules,
            ["Auth.UserNotFound"] =
                "Nie znaleziono konta.",
            ["Auth.InvalidCurrentPassword"] =
                "Aktualne hasło jest nieprawidłowe.",
            ["Auth.PasswordChangeFailed"] =
                "Nie udało się zmienić hasła. Nowe hasło musi spełniać wymagania: " + PasswordRules,
            ["Auth.InvalidPasswordResetToken"] =
                "Link do zmiany hasła jest nieprawidłowy lub wygasł. Poproś o nowy.",
            ["Auth.PasswordResetFailed"] =
                "Nie udało się ustawić nowego hasła. Musi ono spełniać wymagania: " + PasswordRules,
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
            ["Auth.EmailChangeSameAddress"] =
                "Podany adres e-mail jest taki sam jak obecny.",
            ["Auth.InvalidEmailChangeToken"] =
                "Link zmieniający adres e-mail jest nieprawidłowy lub wygasł. Poproś o nowy.",
            ["Auth.EmailChangeFailed"] =
                "Nie udało się zmienić adresu e-mail. Mógł on zostać w międzyczasie zajęty przez inne konto.",

            // ── Auth · per-slice request validation ────────────────────────────────────────────
            // Registration and password reset live on the auth server's own Razor Pages, so these three
            // entries are unreachable from the Frontend today. They stay for map completeness, and they
            // name every rule their validator enforces in case those pages ever move here.
            ["RegisterUser.Validation"] =
                "Formularz rejestracji zawiera nieprawidłowe dane. Sprawdź adres e-mail, nazwę użytkownika "
                + "i wymagane zgody, a hasło musi mieć " + PasswordRules,
            ["ChangePassword.Validation"] =
                "Nowe hasło nie spełnia wymagań: " + PasswordRules,
            ["ForgotPassword.Validation"] =
                "Podany adres e-mail jest nieprawidłowy.",
            ["ResetPassword.Validation"] =
                "Nowe hasło nie spełnia wymagań: " + PasswordRules,
            ["ConfirmEmail.Validation"] =
                "Link potwierdzający adres e-mail jest niekompletny lub nieprawidłowy.",
            ["ResendEmailConfirmation.Validation"] =
                "Podany adres e-mail jest nieprawidłowy.",
            ["DeleteAccount.Validation"] =
                "Formularz usunięcia konta zawiera nieprawidłowe dane.",
            ["CancelAccountDeletion.Validation"] =
                "Link anulujący usunięcie konta jest niekompletny lub nieprawidłowy.",
            ["RequestEmailChange.Validation"] =
                "Podany adres e-mail jest nieprawidłowy.",

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
    /// The mapped codes whose English message says something the Polish text cannot: a line number, a
    /// row key, a duplicate fragment key, or the removal counters. Only for those is the API wording
    /// worth showing (ADR-0044 §4, amended by #703). Everywhere else the English only restates the
    /// Polish headline, which reads like a leak.
    /// <para>
    /// The list is short on purpose, and it is a trade, not a free win: a few other codes do carry a
    /// runtime message (the Identity failures behind <c>Auth.*Failed</c>, the rule names behind
    /// <c>*.Validation</c>). Hiding those is the accepted cost, paid back in Polish copy that says the
    /// same thing — see <see cref="PasswordRules"/>. Adding a code here is the wrong way to pay it,
    /// because the drawer is English on a Polish page.
    /// </para>
    /// </summary>
    private static readonly FrozenSet<string> CodesWhoseApiMessageCarriesData =
        FrozenSet.ToFrozenSet(
        [
            "Import.ParseFailed",
            "Import.InvalidRow",
            "Import.DuplicateFragmentKey",
            "Import.MassRemovalBlocked"
        ], StringComparer.Ordinal);

    /// <summary>
    /// The fallback for an API failure whose code has no text yet: say what the status means, in Polish,
    /// instead of showing the API's English. It deliberately wins over the call site's own fallback,
    /// which only says that something went wrong.
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
    /// Polish sentence for this situation, used when there is no problem object at all, or when a problem
    /// the Frontend wrote carries no text.
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
            // Already Polish. Both the title and the detail are meant for the user, so we keep both.
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

        // No code at all is what UseStatusCodePages produces: a bare English phrase such as
        // "Unauthorized". It is handled exactly like a code we do not know, and it cannot be translated
        // by hand either, so the text for the status covers both.
        string degraded = problem.Status is { } status && PolishByStatusCode.TryGetValue(status, out string? byStatus)
            ? byStatus
            : fallback;

        return new ApiProblemView(degraded, null, technicalDetail, errorCode);
    }

    /// <summary>
    /// A problem the Frontend created itself: a transport failure from Polly, a rel the server does not
    /// send, or a check inside a form. It is Polish already and is never translated.
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
    /// A failure whose body had nothing to translate: an error body that was missing or unreadable, such
    /// as the HTML a reverse proxy sends when the service behind it is down. It is left unmarked on
    /// purpose, so <see cref="Describe"/> falls back through the status texts (ADR-0044 §3). Marking it
    /// would claim a placeholder is "already Polish" and skip that fallback entirely (#637).
    /// </summary>
    public static ProblemDetails StatusOnly(int status)
        => new()
        {
            Status = status
        };

    /// <summary>
    /// Removes the Frontend-authored marker from a problem read off the wire, so an API response that
    /// carries that field, which ours never do, cannot pass its English through untranslated.
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
    /// The same translation for a route that answers with a plain problem body instead of a page, such as
    /// the file-download endpoints, whose failure the browser shows as it is.
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

        // The trace id is the one value that links a support report to the server log. It belongs to the
        // API and is not ours to create, so we pass it through instead of losing it.
        if (problem?.Extensions.TryGetValue(TraceIdExtensionKey, out object? traceId) is true)
        {
            localized.Extensions[TraceIdExtensionKey] = traceId;
        }

        return localized;
    }

    /// <summary>
    /// Reports an API error code that shipped before its Polish text existed. Both renderers use it, so
    /// the gap is never silent, whichever page hit it (ADR-0044 §3).
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
    /// The <c>errorCode</c> arrives as a <see cref="JsonElement"/> when the problem was read off the
    /// wire, and as a plain <see cref="string"/> when it was built here.
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
    /// The code and the API's own wording, whichever of the two the problem carries — but only when
    /// they add something. A problem with neither, which is a status with no body, has nothing worth
    /// showing either.
    /// </summary>
    private static string? BuildTechnicalDetail(string? errorCode, ProblemDetails problem)
    {
        if (errorCode is not null && AddsNothingToThePolishCopy(errorCode))
        {
            return null;
        }

        return (errorCode, FirstNonBlank(problem.Detail, problem.Title)) switch
        {
            (not null, { } apiMessage) => $"{errorCode} — {apiMessage}",
            (not null, null) => errorCode,
            (null, { } apiMessage) => apiMessage,
            _ => null
        };
    }

    /// <summary>
    /// True when the Polish headline is already the whole message: the code has copy here, and its
    /// English carries no data that copy is missing (#703).
    /// <para>
    /// An unmapped code is never hidden. The user only sees the generic sentence there, so the code and
    /// the API's wording are the one thing that makes such a failure reportable.
    /// </para>
    /// </summary>
    private static bool AddsNothingToThePolishCopy(string errorCode)
        => PolishByErrorCode.ContainsKey(errorCode) && !CodesWhoseApiMessageCarriesData.Contains(errorCode);

    private static string? FirstNonBlank(params string?[] candidates)
        => Array.Find(candidates, candidate => !string.IsNullOrWhiteSpace(candidate));
}
