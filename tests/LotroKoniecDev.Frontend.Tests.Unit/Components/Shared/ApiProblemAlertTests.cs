using AngleSharp.Dom;
using LotroKoniecDev.Frontend.Components.Shared;
using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Tests.Unit.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Shared;

/// <summary>
/// The one component every page renders a failed call through (#548 / ADR-0044). These lock down what
/// reaches the screen: Polish copy for an API failure, the API's English behind a collapsed
/// <c>details</c> only where it adds information (#703), and a logged warning when a code shipped
/// without copy.
/// </summary>
public sealed class ApiProblemAlertTests : BunitContext
{
    private readonly CapturingLoggerProvider _loggerProvider = new();

    public ApiProblemAlertTests()
    {
        Services.AddLogging(builder => builder.AddProvider(_loggerProvider));
    }

    [Fact]
    public void Render_ForAMappedApiFailure_ShowsPolishAndKeepsTheEnglishOutOfTheHeadline()
    {
        ProblemDetails problem = ApiProblem(
            "GameVersionEntity.LotroNotationVersion.AlreadyTaken",
            422,
            "Data Conflict",
            "The lotronotationversion value '48.0' is already taken.");

        IRenderedComponent<ApiProblemAlert> component = Render<ApiProblemAlert>(parameters => parameters
            .Add(alert => alert.Problem, problem)
            .Add(alert => alert.Fallback, "Operacja nie powiodła się."));

        component.Find(".problem-headline").TextContent
            .ShouldBe("Taka wersja gry jest już zarejestrowana.");
        component.Find(".problem-headline").TextContent.ShouldNotContain("already taken");
    }

    [Fact]
    public void Render_ForAStatusOnlyProblem_ShowsTheStatusCopyAloneAndNeverAnEmptyBox()
    {
        // What an outage leaves: a status and nothing else (#637). There is no English to collapse,
        // so the headline has to carry the whole message on its own.
        IRenderedComponent<ApiProblemAlert> component = Render<ApiProblemAlert>(parameters => parameters
            .Add(alert => alert.Problem, ApiProblemCopy.StatusOnly(502))
            .Add(alert => alert.Fallback, "Nie udało się wczytać listy wersji gry."));

        component.Find(".problem-headline").TextContent
            .ShouldBe("Usługa jest chwilowo niedostępna. Spróbuj ponownie za chwilę.");
        component.FindAll("details").ShouldBeEmpty();
    }

    [Fact]
    public void Render_ForAnImportFailureWhoseEnglishCarriesData_KeepsTheCodeAndEnglishInACollapsedDetailsBlock()
    {
        ProblemDetails problem = ApiProblem(
            "Import.ParseFailed",
            422,
            "Data Conflict",
            "The upload has 3 unparseable line(s); first failure — line 42: unexpected pipe.");

        IRenderedComponent<ApiProblemAlert> component = Render<ApiProblemAlert>(parameters => parameters
            .Add(alert => alert.Problem, problem)
            .Add(alert => alert.Fallback, "Nie udało się zaimportować pliku."));

        IElement details = component.Find("details.problem-tech");
        details.HasAttribute("open").ShouldBeFalse();
        details.QuerySelector("summary")!.TextContent.ShouldBe("Szczegóły techniczne");
        details.QuerySelector(".problem-tech-body")!.TextContent
            .ShouldBe("Import.ParseFailed — The upload has 3 unparseable line(s); first failure — line 42: unexpected pipe.");
    }

    [Fact]
    public void Render_ForAMappedFailureWhoseEnglishOnlyRestatesThePolish_RendersNoDetailsElementAtAll()
    {
        // The defect of #703, reported from /account/change-password: the drawer held
        // "Auth.InvalidCurrentPassword — The current password is incorrect.", which is the headline
        // again in English. Nothing to expand means nothing that reads like a leak.
        ProblemDetails problem = ApiProblem(
            "Auth.InvalidCurrentPassword",
            400,
            "Validation Error",
            "The current password is incorrect.");

        IRenderedComponent<ApiProblemAlert> component = Render<ApiProblemAlert>(parameters => parameters
            .Add(alert => alert.Problem, problem)
            .Add(alert => alert.Fallback, "Nie udało się zmienić hasła."));

        component.Find(".problem-headline").TextContent.ShouldBe("Aktualne hasło jest nieprawidłowe.");
        component.FindAll("details").ShouldBeEmpty();
        component.Markup.ShouldNotContain("Szczegóły techniczne");
    }

    [Fact]
    public void Render_ForAnUnmappedErrorCode_StillShowsTheCodeAndTheApiWording()
    {
        // Here the user only gets the generic status sentence, so the English is the one thing that
        // makes the failure reportable. It stays until the code has Polish copy.
        ProblemDetails problem = ApiProblem("Catalog.SomethingNobodyMappedYet", 403, "Forbidden", "Not your row.");

        IRenderedComponent<ApiProblemAlert> component = Render<ApiProblemAlert>(parameters => parameters
            .Add(alert => alert.Problem, problem)
            .Add(alert => alert.Fallback, "Operacja nie powiodła się."));

        component.Find("details.problem-tech .problem-tech-body").TextContent
            .ShouldBe("Catalog.SomethingNobodyMappedYet — Not your row.");
    }

    [Fact]
    public void Render_ForAFrontendAuthoredProblem_ShowsBothPolishLinesAndNoTechnicalDetails()
    {
        ProblemDetails problem = ApiProblemCopy.FrontendAuthored(
            "Nie można zaimportować do tej wersji gry.",
            "Serwer nie udostępnia importu dla wybranej wersji. Odśwież stronę i wybierz inną wersję.");

        IRenderedComponent<ApiProblemAlert> component = Render<ApiProblemAlert>(parameters => parameters
            .Add(alert => alert.Problem, problem)
            .Add(alert => alert.Fallback, "Nie udało się zaimportować pliku."));

        component.Find(".problem-headline").TextContent.ShouldBe("Nie można zaimportować do tej wersji gry.");
        component.Find(".problem-secondary").TextContent
            .ShouldBe("Serwer nie udostępnia importu dla wybranej wersji. Odśwież stronę i wybierz inną wersję.");
        component.FindAll("details.problem-tech").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WithNoProblem_ShowsTheCallSiteFallbackAlone()
    {
        IRenderedComponent<ApiProblemAlert> component = Render<ApiProblemAlert>(parameters => parameters
            .Add(alert => alert.Fallback, "Nie udało się wczytać statystyk."));

        component.Find(".problem-headline").TextContent.ShouldBe("Nie udało się wczytać statystyk.");
        component.FindAll(".problem-secondary").ShouldBeEmpty();
        component.FindAll("details.problem-tech").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WithAHeadlineClass_KeepsTheCallersBoxStylingIntact()
    {
        // The rich `error-message` box styles its title through `.em-body .t`; the component has to
        // carry that class or every load-failure box loses its heading style.
        IRenderedComponent<ApiProblemAlert> component = Render<ApiProblemAlert>(parameters => parameters
            .Add(alert => alert.Fallback, "Nie udało się wczytać statystyk.")
            .Add(alert => alert.HeadlineClass, "t"));

        component.Find(".problem-headline").ClassList.ShouldContain("t");
    }

    [Fact]
    public void Render_ForAnUnmappedErrorCode_ShowsTheStatusCopyAndLogsTheGap()
    {
        ProblemDetails problem = ApiProblem("Catalog.SomethingNobodyMappedYet", 403, "Forbidden", "Not your row.");

        IRenderedComponent<ApiProblemAlert> component = Render<ApiProblemAlert>(parameters => parameters
            .Add(alert => alert.Problem, problem)
            .Add(alert => alert.Fallback, "Operacja nie powiodła się."));

        component.Find(".problem-headline").TextContent
            .ShouldBe("Nie masz uprawnień do wykonania tej operacji.");
        _loggerProvider.Entries.ShouldContain(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("Catalog.SomethingNobodyMappedYet"));
    }

    [Fact]
    public void Render_ForAMappedErrorCode_LogsNothing()
    {
        ProblemDetails problem = ApiProblem("Auth.InvalidCurrentPassword", 400, "Validation Error", "Wrong.");

        Render<ApiProblemAlert>(parameters => parameters
            .Add(alert => alert.Problem, problem)
            .Add(alert => alert.Fallback, "Nie udało się zmienić hasła."));

        _loggerProvider.Entries.ShouldBeEmpty();
    }

    private static ProblemDetails ApiProblem(string errorCode, int status, string title, string detail)
        => new()
        {
            Title = title,
            Detail = detail,
            Status = status,
            Extensions = { [ApiProblemCopy.ErrorCodeExtensionKey] = errorCode }
        };
}
