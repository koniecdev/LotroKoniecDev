using System.Net;
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
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Contracts.Translators;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;
using TranslationDiscoveryResponse = LotroKoniecDev.TranslationSystem.Contracts.Discovery.DiscoveryResponse;
using TranslationRels = LotroKoniecDev.TranslationSystem.Contracts.Hateoas.Rels;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Account;

/// <summary>
/// Calls the GDPR export download route's handler directly, with no web host. On success it has to join
/// the auth part with the TMS contribution part (ADR-0032) into an indented camelCase JSON attachment.
/// When the auth part fails it has to return the API's problem, or our own 502. When the TMS part fails
/// it must still serve the file, marked <c>isComplete: false</c>.
/// </summary>
public sealed class AccountEndpointsExtensionsTests
{
    private const string BaseUrl = "https://localhost:5003/";
    private const string TmsBaseUrl = "https://localhost:5002/";
    private const string ExportHref = "auth/account/data-export";
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
            loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

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
            loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

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
            loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

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
            loader, _discoveryCache, tmsClient, NullLoggerFactory.Instance, CancellationToken.None);

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
            loader, _discoveryCache, tmsClient, NullLoggerFactory.Instance, CancellationToken.None);

        FileContentHttpResult file = result.ShouldBeOfType<FileContentHttpResult>();
        file.ContentType.ShouldBe("application/json");
        file.FileDownloadName!.ShouldStartWith("lotro-translator-moje-dane-");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTheContributionRelIsNotAdvertised_ServesTheFileWithIsCompleteFalse()
    {
        // A rel we cannot resolve is treated exactly like a failed TMS call (ADR-0032): the Art. 15
        // document still downloads and is honestly marked incomplete. It must never fall back to a
        // guessed path, and it must never fail the auth part's download either.
        AccountLoader loader = CreateLoaderReturning(AccountLoaderTests.CreateEnvelope());
        _discoveryCache.GetTranslationSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(new TranslationDiscoveryResponse("LotroKoniecDev.TranslationSystem")));
        StubHttpMessageHandler tmsHandler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(CreateContribution(), ApiJsonOptions));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            loader, _discoveryCache, CreateTmsClient(tmsHandler), NullLoggerFactory.Instance, CancellationToken.None);

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
        // A TMS outage only makes the file incomplete. Only the auth part can fail the download.
        AccountLoader loader = CreateLoaderReturning(AccountLoaderTests.CreateEnvelope());
        _discoveryCache.GetTranslationSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<TranslationDiscoveryResponse>(new ProblemDetails { Status = 503 }));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        FileContentHttpResult file = result.ShouldBeOfType<FileContentHttpResult>();
        string json = Encoding.UTF8.GetString(file.FileContents.ToArray());
        json.ShouldContain("\"translationData\": null");
        json.ShouldContain("\"isComplete\": false");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTmsReturnsAMalformedBody_StillServesTheFileWithIsCompleteFalse()
    {
        // A 200 whose body is not JSON counts as a failed TMS part. The HTTP layer turns it into a
        // failure (#638), and the route marks the file incomplete instead of failing the download
        // (ADR-0032).
        AccountLoader loader = CreateLoaderReturning(AccountLoaderTests.CreateEnvelope());
        ITranslationSystemClient tmsClient = CreateTmsClient(
            StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "this is not json"));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            loader, _discoveryCache, tmsClient, NullLoggerFactory.Instance, CancellationToken.None);

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
            loader, _discoveryCache, tmsClient, NullLoggerFactory.Instance, CancellationToken.None);

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
            loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

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
            loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(StatusCodes.Status502BadGateway);
        problem.ProblemDetails.Title.ShouldBe("Usługa jest chwilowo niedostępna. Spróbuj ponownie za chwilę.");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTheAuthLegAnswersAMaintenancePageWithA200_ServesThePolishStatusCopyInsteadOfThrowing()
    {
        // The #637 outage with a success status: the proxy's maintenance page arrives as 200 with HTML.
        // That used to throw a JsonException out of the route (#638). It is a failed auth part like any
        // other and gives the same Polish sentence the proxy's own 502 does.
        StubDiscoveryWithExportLink();
        AccountLoader loader = new(
            _discoveryCache,
            CreateClient(StubHttpMessageHandler.RespondWith(
                HttpStatusCode.OK,
                "<html><head><title>Maintenance</title></head><body>Back soon</body></html>")));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(StatusCodes.Status502BadGateway);
        problem.ProblemDetails.Title.ShouldBe("Usługa jest chwilowo niedostępna. Spróbuj ponownie za chwilę.");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenUpstreamReturnsAnEnglishProblem_RewritesItInPolish()
    {
        // A download route answers with a raw problem body the browser shows verbatim, so it carries
        // the same errorCode→Polish rule as a rendered page (#548 / ADR-0044).
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
            loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(404);
        problem.ProblemDetails.Title.ShouldBe("Nie znaleziono konta.");
        problem.ProblemDetails.Extensions[ApiProblemCopy.TechnicalDetailExtensionKey]
            .ShouldBe("Auth.UserNotFound — User not found.");
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
            loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(503);
    }

    private AccountLoader CreateLoaderReturning(AccountDataExportResponse envelope)
    {
        StubDiscoveryWithExportLink();
        return new AccountLoader(
            _discoveryCache,
            CreateClient(StubHttpMessageHandler.RespondWith(
                HttpStatusCode.OK,
                JsonSerializer.Serialize(envelope, ApiJsonOptions))));
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
