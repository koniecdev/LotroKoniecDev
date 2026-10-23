using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.Frontend.Components.Pages.Account;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Tests.Unit.Shared;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Contracts.Translators;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;
using TranslationDiscoveryResponse = LotroKoniecDev.TranslationSystem.Contracts.Discovery.DiscoveryResponse;
using TranslationRels = LotroKoniecDev.TranslationSystem.Contracts.Hateoas.Rels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Account;

/// <summary>
/// Calls the GDPR export route's handler directly, with no web host. Only the auth part can fail the
/// download; a failed TMS part marks the file <c>isComplete: false</c> (ADR-0032).
/// </summary>
public sealed class AccountEndpointsExtensionsTests
{
    private const string BaseUrl = "https://localhost:5003/";
    private const string TmsBaseUrl = "https://localhost:5002/";
    private const string ExportHref = "auth/account/data-export";
    private const string DownloadHref = "advertised/account-download";
    private const string ClientIpAddress = "203.0.113.7";
    private const string ClientUserAgent = "Mozilla/5.0 (QA)";
    private const string CorrectPassword = "Correct-Horse-1!";
    private const string ContributionExportHref = "/advertised/my-contribution-export";

    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IDiscoveryCache _discoveryCache = Substitute.For<IDiscoveryCache>();

    [Fact]
    public async Task DownloadAccountExportAsync_OnSuccess_ReturnsAJsonFileAttachment()
    {
        AccountLoader loader = CreateLoaderReturning(AccountLoaderTests.CreateEnvelope());

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        FileContentHttpResult file = result.ShouldBeOfType<FileContentHttpResult>();
        file.ContentType.ShouldBe("application/json");
        file.FileDownloadName!.ShouldStartWith("lotro-translator-moje-dane-");
        file.FileDownloadName.ShouldEndWith(".json");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_OnSuccess_SerializesTheComposedDocumentAsIndentedCamelCaseJson()
    {
        AccountLoader loader = CreateLoaderReturning(AccountLoaderTests.CreateEnvelope());

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        FileContentHttpResult file = result.ShouldBeOfType<FileContentHttpResult>();
        string json = Encoding.UTF8.GetString(file.FileContents.ToArray());
        json.ShouldContain("\"username\": \"frodo\"");
        json.ShouldContain("\"email\": \"frodo@shire.me\"");
        json.ShouldContain("\"isComplete\": true");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTmsLegSucceeds_TheFileCarriesTheContributionData()
    {
        AccountLoader loader = CreateLoaderReturning(AccountLoaderTests.CreateEnvelope());

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        FileContentHttpResult file = result.ShouldBeOfType<FileContentHttpResult>();
        string json = Encoding.UTF8.GetString(file.FileContents.ToArray());
        json.ShouldContain("\"displayName\": \"Frodo Baggins\"");
        json.ShouldContain("\"submittedTotal\": 2");
        json.ShouldContain("\"approvedTotal\": 1");
        json.ShouldContain("\"status\": \"Draft\"");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTmsLegFails_StillServesTheAuthDataWithIsCompleteFalse()
    {
        AccountLoader loader = CreateLoaderReturning(AccountLoaderTests.CreateEnvelope());
        ITranslationSystemClient tmsClient = CreateTmsClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.ServiceUnavailable,
            """{ "title": "Usługa chwilowo niedostępna", "status": 503 }"""));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, tmsClient, NullLoggerFactory.Instance, CancellationToken.None);

        FileContentHttpResult file = result.ShouldBeOfType<FileContentHttpResult>();
        string json = Encoding.UTF8.GetString(file.FileContents.ToArray());
        json.ShouldContain("\"username\": \"frodo\"");
        json.ShouldContain("\"translationData\": null");
        json.ShouldContain("\"isComplete\": false");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTmsLegFails_TheFileNameStaysTheExportAttachment()
    {
        AccountLoader loader = CreateLoaderReturning(AccountLoaderTests.CreateEnvelope());
        ITranslationSystemClient tmsClient = CreateTmsClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.InternalServerError,
            """{ "title": "Błąd serwera", "status": 500 }"""));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, tmsClient, NullLoggerFactory.Instance, CancellationToken.None);

        FileContentHttpResult file = result.ShouldBeOfType<FileContentHttpResult>();
        file.ContentType.ShouldBe("application/json");
        file.FileDownloadName!.ShouldStartWith("lotro-translator-moje-dane-");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTheContributionRelIsNotAdvertised_ServesTheFileWithIsCompleteFalse()
    {
        // A rel we cannot resolve counts as a failed TMS call, never a guessed path (ADR-0032).
        AccountLoader loader = CreateLoaderReturning(AccountLoaderTests.CreateEnvelope());
        _discoveryCache.GetTranslationSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(new TranslationDiscoveryResponse("LotroKoniecDev.TranslationSystem")));
        StubHttpMessageHandler tmsHandler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(CreateContribution(), ApiJsonOptions));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, CreateTmsClient(tmsHandler), NullLoggerFactory.Instance, CancellationToken.None);

        FileContentHttpResult file = result.ShouldBeOfType<FileContentHttpResult>();
        string json = Encoding.UTF8.GetString(file.FileContents.ToArray());
        json.ShouldContain("\"translationData\": null");
        json.ShouldContain("\"isComplete\": false");
        // No rel means no call: we never guessed the contribution endpoint.
        tmsHandler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTmsDiscoveryIsUnavailable_ServesTheFileWithIsCompleteFalse()
    {
        AccountLoader loader = CreateLoaderReturning(AccountLoaderTests.CreateEnvelope());
        _discoveryCache.GetTranslationSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<TranslationDiscoveryResponse>(new ProblemDetails { Status = 503 }));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        FileContentHttpResult file = result.ShouldBeOfType<FileContentHttpResult>();
        string json = Encoding.UTF8.GetString(file.FileContents.ToArray());
        json.ShouldContain("\"translationData\": null");
        json.ShouldContain("\"isComplete\": false");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTmsReturnsAMalformedBody_StillServesTheFileWithIsCompleteFalse()
    {
        // A 200 whose body is not JSON counts as a failed TMS part (#638).
        AccountLoader loader = CreateLoaderReturning(AccountLoaderTests.CreateEnvelope());
        ITranslationSystemClient tmsClient = CreateTmsClient(
            StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "this is not json"));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, tmsClient, NullLoggerFactory.Instance, CancellationToken.None);

        FileContentHttpResult file = result.ShouldBeOfType<FileContentHttpResult>();
        string json = Encoding.UTF8.GetString(file.FileContents.ToArray());
        json.ShouldContain("\"translationData\": null");
        json.ShouldContain("\"isComplete\": false");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTmsReturnsAnEmptyBody_StillServesTheFileWithIsCompleteFalse()
    {
        AccountLoader loader = CreateLoaderReturning(AccountLoaderTests.CreateEnvelope());
        ITranslationSystemClient tmsClient = CreateTmsClient(
            StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, tmsClient, NullLoggerFactory.Instance, CancellationToken.None);

        FileContentHttpResult file = result.ShouldBeOfType<FileContentHttpResult>();
        string json = Encoding.UTF8.GetString(file.FileContents.ToArray());
        json.ShouldContain("\"translationData\": null");
        json.ShouldContain("\"isComplete\": false");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenUpstreamReturnsProblem_SurfacesThatProblem()
    {
        StubDiscoveryWithExportLink();
        AccountLoader loader = new(
            _discoveryCache,
            CreateClient(StubHttpMessageHandler.RespondWith(
                HttpStatusCode.NotFound,
                """{ "title": "Nie znaleziono użytkownika", "status": 404 }""")));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(404);
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTheProxyAnswersForAStoppedUpstream_ServesThePolishStatusCopy()
    {
        StubDiscoveryWithExportLink();
        AccountLoader loader = new(
            _discoveryCache,
            CreateClient(StubHttpMessageHandler.RespondWith(
                HttpStatusCode.BadGateway,
                "<html><head><title>502 Bad Gateway</title></head><body></body></html>")));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(StatusCodes.Status502BadGateway);
        problem.ProblemDetails.Title.ShouldBe("Usługa jest chwilowo niedostępna. Spróbuj ponownie za chwilę.");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTheAuthLegAnswersAMaintenancePageWithA200_ServesThePolishStatusCopyInsteadOfThrowing()
    {
        // The proxy's maintenance page arrives as 200 with HTML (#638). It must read like the proxy's
        // own 502.
        StubDiscoveryWithExportLink();
        AccountLoader loader = new(
            _discoveryCache,
            CreateClient(StubHttpMessageHandler.RespondWith(
                HttpStatusCode.OK,
                "<html><head><title>Maintenance</title></head><body>Back soon</body></html>")));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(StatusCodes.Status502BadGateway);
        problem.ProblemDetails.Title.ShouldBe("Usługa jest chwilowo niedostępna. Spróbuj ponownie za chwilę.");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenUpstreamReturnsAnEnglishProblem_RewritesItInPolish()
    {
        // The browser shows a download route's problem body as it is, so the same Polish rule applies
        // (ADR-0044).
        StubDiscoveryWithExportLink();
        AccountLoader loader = new(
            _discoveryCache,
            CreateClient(StubHttpMessageHandler.RespondWith(
                HttpStatusCode.NotFound,
                """
                {
                  "title": "Not Found",
                  "status": 404,
                  "detail": "User not found.",
                  "errorCode": "Auth.UserNotFound",
                  "traceId": "00-abc-def-01"
                }
                """)));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(404);
        problem.ProblemDetails.Title.ShouldBe("Nie znaleziono konta.");
        // The English only restates the Polish, so it is dropped (#703); the trace id ties a report to
        // the server log.
        problem.ProblemDetails.Extensions.ShouldNotContainKey(ApiProblemCopy.TechnicalDetailExtensionKey);
        // Read off the wire, so the value is a JsonElement rather than a string.
        problem.ProblemDetails.Extensions[ApiProblemCopy.TraceIdExtensionKey]!.ToString().ShouldBe("00-abc-def-01");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenDiscoveryFails_SurfacesTheDiscoveryProblem()
    {
        _discoveryCache.GetAuthSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<AuthDiscoveryResponse>(new ProblemDetails
            {
                Title = "Usługa chwilowo niedostępna",
                Status = 503
            }));
        AccountLoader loader = new(
            _discoveryCache,
            CreateClient(StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "{}")));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(503);
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenThePasswordFieldIsEmpty_SendsTheUserBackToThePageWithoutCallingTheApi()
    {
        StubDiscoveryWithExportLink();
        StubHttpMessageHandler authHandler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            RepresentationJson());
        AccountLoader loader = new(_discoveryCache, CreateClient(authHandler));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            "   ", HttpContextWithClient(), loader, _discoveryCache, CreateTmsClientReturningContribution(),
            NullLoggerFactory.Instance, CancellationToken.None);

        RedirectHttpResult redirect = result.ShouldBeOfType<RedirectHttpResult>();
        redirect.Url.ShouldBe("/account/export?error=required");
        // An empty field never reaches the auth API: there is nothing for it to check.
        authHandler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenThePasswordIsWrong_SendsTheUserBackToThePageAndServesNoFile()
    {
        StubDiscoveryWithExportLink();
        AccountLoader loader = new(
            _discoveryCache,
            CreateClient(StubHttpMessageHandler.RespondWith(
                HttpStatusCode.OK,
                RepresentationJson(),
                HttpStatusCode.BadRequest,
                """
                {
                  "title": "Validation Error",
                  "status": 400,
                  "detail": "The current password is incorrect.",
                  "errorCode": "Auth.InvalidCurrentPassword"
                }
                """)));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            "wrong", HttpContextWithClient(), loader, _discoveryCache, CreateTmsClientReturningContribution(),
            NullLoggerFactory.Instance, CancellationToken.None);

        RedirectHttpResult redirect = result.ShouldBeOfType<RedirectHttpResult>();
        redirect.Url.ShouldBe("/account/export?error=password");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTheSessionExpired_SendsTheUserBackToTheCleanPage()
    {
        // No marker: a "session expired" sentence would greet the user right after the new login.
        StubDiscoveryWithExportLink();
        AccountLoader loader = new(
            _discoveryCache,
            CreateClient(StubHttpMessageHandler.RespondWith(
                HttpStatusCode.OK,
                RepresentationJson(),
                HttpStatusCode.Unauthorized,
                """{ "title": "Unauthorized", "status": 401 }""")));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, CreateTmsClientReturningContribution(),
            NullLoggerFactory.Instance, CancellationToken.None);

        RedirectHttpResult redirect = result.ShouldBeOfType<RedirectHttpResult>();
        redirect.Url.ShouldBe("/account/export");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTheAccountsConfirmationBudgetIsSpent_SendsTheUserBackWithThePasswordThrottledMarker()
    {
        // The account's own budget (ADR-0053): a 429 with a code and a 15-minute window.
        StubDiscoveryWithExportLink();
        AccountLoader loader = new(
            _discoveryCache,
            CreateClient(StubHttpMessageHandler.RespondWith(
                HttpStatusCode.OK,
                RepresentationJson(),
                HttpStatusCode.TooManyRequests,
                """{ "title": "Too Many Requests", "status": 429, "errorCode": "Auth.PasswordConfirmationThrottled" }""")));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, CreateTmsClientReturningContribution(),
            NullLoggerFactory.Instance, CancellationToken.None);

        RedirectHttpResult redirect = result.ShouldBeOfType<RedirectHttpResult>();
        redirect.Url.ShouldBe("/account/export?error=password-throttled");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTheAuthApiThrottles_SendsTheUserBackToThePageWithTheThrottledMarker()
    {
        // A bare 429 is the per-address limit (ADR-0054). A problem page is the wrong answer to "come back
        // in a minute".
        StubDiscoveryWithExportLink();
        AccountLoader loader = new(
            _discoveryCache,
            CreateClient(StubHttpMessageHandler.RespondWith(
                HttpStatusCode.OK,
                RepresentationJson(),
                HttpStatusCode.TooManyRequests,
                """{ "title": "Too Many Requests", "status": 429 }""")));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, CreateTmsClientReturningContribution(),
            NullLoggerFactory.Instance, CancellationToken.None);

        RedirectHttpResult redirect = result.ShouldBeOfType<RedirectHttpResult>();
        redirect.Url.ShouldBe("/account/export?error=throttled");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenA400CarriesNoErrorCode_ServesTheProblemInsteadOfBlamingThePassword()
    {
        // The error code decides, not the status, so a later validation rule never reads as "wrong
        // password".
        StubDiscoveryWithExportLink();
        AccountLoader loader = new(
            _discoveryCache,
            CreateClient(StubHttpMessageHandler.RespondWith(
                HttpStatusCode.OK,
                RepresentationJson(),
                HttpStatusCode.BadRequest,
                """{ "title": "Bad Request", "status": 400 }""")));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, CreateTmsClientReturningContribution(),
            NullLoggerFactory.Instance, CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTheAuthApiRefusesThePassword_NeverCallsTheTmsLeg()
    {
        // The password gates the whole composed document, not only the auth half (#690, ADR-0052).
        StubDiscoveryWithExportLink();
        AccountLoader loader = new(
            _discoveryCache,
            CreateClient(StubHttpMessageHandler.RespondWith(
                HttpStatusCode.OK,
                RepresentationJson(),
                HttpStatusCode.BadRequest,
                """{ "title": "Validation Error", "status": 400, "errorCode": "Auth.InvalidCurrentPassword" }""")));
        StubHttpMessageHandler tmsHandler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(CreateContribution(), ApiJsonOptions));

        await AccountEndpointsExtensions.DownloadAccountExportAsync(
            "wrong", HttpContextWithClient(), loader, _discoveryCache, CreateTmsClient(tmsHandler),
            NullLoggerFactory.Instance, CancellationToken.None);

        tmsHandler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task DownloadAccountExportAsync_OnSuccess_PostsToTheDownloadLinkTheAccountAdvertises()
    {
        StubDiscoveryWithExportLink();
        StubHttpMessageHandler authHandler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            RepresentationJson());
        AccountLoader loader = new(_discoveryCache, CreateClient(authHandler));

        await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, CreateTmsClientReturningContribution(),
            NullLoggerFactory.Instance, CancellationToken.None);

        authHandler.LastRequest.ShouldNotBeNull();
        authHandler.LastRequest.Method.ShouldBe(HttpMethod.Post);
        authHandler.LastRequest.RequestUri!.ToString().ShouldBe($"{BaseUrl}{DownloadHref}");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTheExportIsA200WithNoAuthData_ServesABadGatewayInsteadOfThrowing()
    {
        StubDiscoveryWithExportLink();
        AccountLoader loader = new(
            _discoveryCache,
            CreateClient(StubHttpMessageHandler.RespondWith(
                HttpStatusCode.OK,
                RepresentationJson(),
                HttpStatusCode.OK,
                "{}")));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, CreateTmsClientReturningContribution(),
            NullLoggerFactory.Instance, CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(StatusCodes.Status502BadGateway);
    }

    [Fact]
    public async Task DownloadAccountExportAsync_OnSuccess_LogsWhoTookTheFileAndFromWhere()
    {
        // The auth API only sees this service, so this line carries the reader's real address (#690).
        AccountDataExportResponse envelope = AccountLoaderTests.CreateEnvelope();
        AccountLoader loader = CreateLoaderReturning(envelope);
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);

        await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, CreateTmsClientReturningContribution(),
            loggerFactory, CancellationToken.None);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldContain(envelope.AuthData.UserId.ToString());
        entry.Message.ShouldContain(ClientIpAddress);
        entry.Message.ShouldContain(ClientUserAgent);
        entry.Message.ShouldNotContain(envelope.AuthData.Email);
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenThePasswordIsWrong_LogsTheRefusalWithTheApisReason()
    {
        StubDiscoveryWithExportLink();
        AccountLoader loader = new(
            _discoveryCache,
            CreateClient(StubHttpMessageHandler.RespondWith(
                HttpStatusCode.OK,
                RepresentationJson(),
                HttpStatusCode.BadRequest,
                """{ "title": "Validation Error", "status": 400, "errorCode": "Auth.InvalidCurrentPassword" }""")));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);

        await AccountEndpointsExtensions.DownloadAccountExportAsync(
            "wrong", HttpContextWithClient(subject: "subject-42"), loader, _discoveryCache,
            CreateTmsClientReturningContribution(), loggerFactory, CancellationToken.None);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain("subject-42");
        entry.Message.ShouldContain("400");
        entry.Message.ShouldContain("Auth.InvalidCurrentPassword");
        entry.Message.ShouldContain(ClientIpAddress);
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenThePasswordFieldIsEmpty_LogsTheRefusalWithItsReason()
    {
        StubDiscoveryWithExportLink();
        AccountLoader loader = new(
            _discoveryCache,
            CreateClient(StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, RepresentationJson())));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);

        await AccountEndpointsExtensions.DownloadAccountExportAsync(
            "   ", HttpContextWithClient(subject: "subject-42"), loader, _discoveryCache,
            CreateTmsClientReturningContribution(), loggerFactory, CancellationToken.None);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain("subject-42");
        entry.Message.ShouldContain("no password was sent");
        entry.Message.ShouldContain(ClientIpAddress);
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTheAuthApiIsDown_LogsARefusalThatDoesNotReadLikeATypo()
    {
        StubDiscoveryWithExportLink();
        AccountLoader loader = new(
            _discoveryCache,
            CreateClient(StubHttpMessageHandler.RespondWith(
                HttpStatusCode.OK,
                RepresentationJson(),
                HttpStatusCode.ServiceUnavailable,
                """{ "title": "Service Unavailable", "status": 503 }""")));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);

        await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache,
            CreateTmsClientReturningContribution(), loggerFactory, CancellationToken.None);

        logs.Entries.ShouldContain(entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains("503", StringComparison.Ordinal)
            && entry.Message.Contains("the auth API call failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTheAccountDoesNotAdvertiseTheDownload_ServesAProblemAndNoFile()
    {
        // We never compose the path ourselves (#610).
        StubDiscoveryWithExportLink();
        AccountLoader loader = new(
            _discoveryCache,
            CreateClient(StubHttpMessageHandler.RespondWith(
                HttpStatusCode.OK,
                JsonSerializer.Serialize(AccountLoaderTests.CreateEnvelope(links: []), ApiJsonOptions))));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            CorrectPassword, HttpContextWithClient(), loader, _discoveryCache, CreateTmsClientReturningContribution(),
            NullLoggerFactory.Instance, CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void MapAccountEndpoints_TheDownloadRoute_AnswersOnlyToAPost()
    {
        // A GET would be reachable by a link somebody else wrote.
        DownloadRoute().Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.ShouldBe(["POST"]);
    }

    [Fact]
    public void MapAccountEndpoints_TheDownloadRoute_NeedsALogin()
    {
        DownloadRoute().Metadata.GetMetadata<IAuthorizeData>().ShouldNotBeNull();
    }

    [Fact]
    public void MapAccountEndpoints_TheDownloadRoute_DemandsTheAntiforgeryToken()
    {
        // The requirement comes from binding a form field, so removing it would silently remove the CSRF
        // protection.
        DownloadRoute().Metadata.GetMetadata<IAntiforgeryMetadata>()!.RequiresValidation.ShouldBeTrue();
    }

    /// <summary>
    /// A bare route builder: a web host would read and watch configuration files, and this suite is pure.
    /// </summary>
    private static RouteEndpoint DownloadRoute()
    {
        BareEndpointRouteBuilder routes = new();
        routes.MapAccountEndpoints();

        return routes.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(route => route.RoutePattern.RawText == AccountEndpointsExtensions.ExportDownloadPath);
    }

    private sealed class BareEndpointRouteBuilder : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();

        public ICollection<EndpointDataSource> DataSources { get; } = [];

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private static HttpContext HttpContextWithClient(string? subject = null)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(ClientIpAddress);
        httpContext.Request.Headers.UserAgent = ClientUserAgent;
        if (subject is not null)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "test"));
        }

        return httpContext;
    }

    /// <summary>
    /// The stub answers the GET and the POST alike, with an envelope that advertises the
    /// <c>download-account-data</c> link.
    /// </summary>
    private AccountLoader CreateLoaderReturning(AccountDataExportResponse envelope)
    {
        StubDiscoveryWithExportLink();
        return new AccountLoader(
            _discoveryCache,
            CreateClient(StubHttpMessageHandler.RespondWith(
                HttpStatusCode.OK,
                JsonSerializer.Serialize(WithDownloadLink(envelope), ApiJsonOptions))));
    }

    private static string RepresentationJson() =>
        JsonSerializer.Serialize(WithDownloadLink(AccountLoaderTests.CreateEnvelope()), ApiJsonOptions);

    /// <summary>
    /// Its href differs from the discovery one, so a POST sent to the wrong document's link shows up.
    /// </summary>
    private static AccountDataExportResponse WithDownloadLink(AccountDataExportResponse envelope)
    {
        envelope.Links = [.. envelope.Links, new LinkDto(DownloadHref, Rels.DownloadAccountData, "POST")];
        return envelope;
    }

    private void StubDiscoveryWithExportLink()
    {
        AuthDiscoveryResponse discovery = new("LotroKoniecDev.AuthSystem")
        {
            Links = [new LinkDto(ExportHref, Rels.ExportAccountData, "GET")]
        };
        _discoveryCache.GetAuthSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(discovery));

        // The TMS leg is addressed by its own rel (#610), so the same cache serves both legs here.
        TranslationDiscoveryResponse translationDiscovery = new("LotroKoniecDev.TranslationSystem")
        {
            Links = [new LinkDto(ContributionExportHref, TranslationRels.ContributionDataExport, "GET")]
        };
        _discoveryCache.GetTranslationSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(translationDiscovery));
    }

    private static AuthSystemClient CreateClient(StubHttpMessageHandler handler)
    {
        HttpClient httpClient = new(handler)
        {
            BaseAddress = new Uri(BaseUrl)
        };
        return new AuthSystemClient(httpClient);
    }

    private static ITranslationSystemClient CreateTmsClientReturningContribution()
        => CreateTmsClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(CreateContribution(), ApiJsonOptions)));

    private static ITranslationSystemClient CreateTmsClient(StubHttpMessageHandler handler)
    {
        HttpClient httpClient = new(handler)
        {
            BaseAddress = new Uri(TmsBaseUrl)
        };
        return new TranslationSystemClient(httpClient);
    }

    private static TranslatorDataExportResponse CreateContribution()
    {
        TranslatorId translatorId = TranslatorId.Create();
        ContributionRowDto draftRow = new(TranslationId.Create(), 620756992, 1001, TranslationStatus.Draft);
        ContributionRowDto approvedRow = new(TranslationId.Create(), 620756992, 1002, TranslationStatus.Approved);

        return new TranslatorDataExportResponse(
            new TranslatorProfileExportDto(
                translatorId,
                IdentityId.Create(),
                "Frodo Baggins",
                "frodo@shire.me",
                new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero)),
            new ContributionSummaryDto(
                SubmittedTotal: 2,
                SubmittedDraft: 1,
                SubmittedApproved: 1,
                SubmittedNeedsReview: 0,
                ApprovedTotal: 1,
                SubmittedRows: [draftRow, approvedRow],
                ApprovedRows: [approvedRow]));
    }
}
