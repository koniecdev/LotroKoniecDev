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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using LotroKoniecDev.Frontend.Tests.Unit.Shared;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Account;

/// <summary>
/// Calls the GDPR export download route's handler directly, with no web host. On success it has to join
/// the auth part with the TMS contribution part (ADR-0032) into an indented camelCase JSON attachment.
/// The auth API checks the password first (#690). When the auth part fails, for a wrong password or
/// anything else, the route sends the browser back to the export page with an error code and serves no
/// data. When the TMS part fails it must still serve the file, marked <c>isComplete: false</c>.
/// </summary>
public sealed class AccountEndpointsExtensionsTests
{
    private const string BaseUrl = "https://localhost:5003/";
    private const string TmsBaseUrl = "https://localhost:5002/";
    private const string AccountHref = "auth/account";
    private const string ExportHref = "auth/account/data-export";
    private const string Password = "S3cret!Password";
    private const string Subject = "5b0d9c1e-7a55-4d0c-9a53-0f4f6f1f3a11";
    private const string ClientIp = "203.0.113.7";
    private const string ClientUserAgent = "Mozilla/5.0 (test)";
    private const string ContributionExportHref = "/advertised/my-contribution-export";

    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IDiscoveryCache _discoveryCache = Substitute.For<IDiscoveryCache>();
    private readonly DefaultHttpContext _httpContext = CreateHttpContext();

    [Fact]
    public async Task DownloadAccountExportAsync_OnSuccess_ReturnsAJsonFileAttachment()
    {
        AccountLoader loader = CreateLoaderReturningExport();

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            Password, _httpContext, loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        FileContentHttpResult file = result.ShouldBeOfType<FileContentHttpResult>();
        file.ContentType.ShouldBe("application/json");
        file.FileDownloadName!.ShouldStartWith("lotro-translator-moje-dane-");
        file.FileDownloadName.ShouldEndWith(".json");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_OnSuccess_SerializesTheComposedDocumentAsIndentedCamelCaseJson()
    {
        AccountLoader loader = CreateLoaderReturningExport();

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            Password, _httpContext, loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        FileContentHttpResult file = result.ShouldBeOfType<FileContentHttpResult>();
        string json = Encoding.UTF8.GetString(file.FileContents.ToArray());
        json.ShouldContain("\"username\": \"frodo\"");
        json.ShouldContain("\"email\": \"frodo@shire.me\"");
        json.ShouldContain("\"isComplete\": true");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTmsLegSucceeds_TheFileCarriesTheContributionData()
    {
        AccountLoader loader = CreateLoaderReturningExport();

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            Password, _httpContext, loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

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
        AccountLoader loader = CreateLoaderReturningExport();
        ITranslationSystemClient tmsClient = CreateTmsClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.ServiceUnavailable,
            """{ "title": "Usługa chwilowo niedostępna", "status": 503 }"""));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            Password, _httpContext, loader, _discoveryCache, tmsClient, NullLoggerFactory.Instance, CancellationToken.None);

        FileContentHttpResult file = result.ShouldBeOfType<FileContentHttpResult>();
        string json = Encoding.UTF8.GetString(file.FileContents.ToArray());
        json.ShouldContain("\"username\": \"frodo\"");
        json.ShouldContain("\"translationData\": null");
        json.ShouldContain("\"isComplete\": false");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTmsLegFails_TheFileNameStaysTheExportAttachment()
    {
        AccountLoader loader = CreateLoaderReturningExport();
        ITranslationSystemClient tmsClient = CreateTmsClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.InternalServerError,
            """{ "title": "Błąd serwera", "status": 500 }"""));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            Password, _httpContext, loader, _discoveryCache, tmsClient, NullLoggerFactory.Instance, CancellationToken.None);

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
        AccountLoader loader = CreateLoaderReturningExport();
        _discoveryCache.GetTranslationSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(new TranslationDiscoveryResponse("LotroKoniecDev.TranslationSystem")));
        StubHttpMessageHandler tmsHandler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(CreateContribution(), ApiJsonOptions));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            Password, _httpContext, loader, _discoveryCache, CreateTmsClient(tmsHandler), NullLoggerFactory.Instance, CancellationToken.None);

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
        AccountLoader loader = CreateLoaderReturningExport();
        _discoveryCache.GetTranslationSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<TranslationDiscoveryResponse>(new ProblemDetails { Status = 503 }));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            Password, _httpContext, loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

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
        AccountLoader loader = CreateLoaderReturningExport();
        ITranslationSystemClient tmsClient = CreateTmsClient(
            StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "this is not json"));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            Password, _httpContext, loader, _discoveryCache, tmsClient, NullLoggerFactory.Instance, CancellationToken.None);

        FileContentHttpResult file = result.ShouldBeOfType<FileContentHttpResult>();
        string json = Encoding.UTF8.GetString(file.FileContents.ToArray());
        json.ShouldContain("\"translationData\": null");
        json.ShouldContain("\"isComplete\": false");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTmsReturnsAnEmptyBody_StillServesTheFileWithIsCompleteFalse()
    {
        AccountLoader loader = CreateLoaderReturningExport();
        ITranslationSystemClient tmsClient = CreateTmsClient(
            StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            Password, _httpContext, loader, _discoveryCache, tmsClient, NullLoggerFactory.Instance, CancellationToken.None);

        FileContentHttpResult file = result.ShouldBeOfType<FileContentHttpResult>();
        string json = Encoding.UTF8.GetString(file.FileContents.ToArray());
        json.ShouldContain("\"translationData\": null");
        json.ShouldContain("\"isComplete\": false");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DownloadAccountExportAsync_WithoutAPassword_GoesBackToTheExportPageAndCallsNoApi(string? password)
    {
        StubHttpMessageHandler authHandler = CreateAuthHandler(HttpStatusCode.OK, ExportJson());
        StubDiscoveryWithAccountLink();
        AccountLoader loader = new(_discoveryCache, CreateClient(authHandler));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            password, _httpContext, loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        RedirectHttpResult redirect = result.ShouldBeOfType<RedirectHttpResult>();
        redirect.Url.ShouldBe("/account/export?error=password-required");
        redirect.AcceptLocalUrlOnly.ShouldBeTrue();
        authHandler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task DownloadAccountExportAsync_PostsThePasswordToTheHrefTheAccountAdvertises()
    {
        StubHttpMessageHandler authHandler = CreateAuthHandler(HttpStatusCode.OK, ExportJson());
        StubDiscoveryWithAccountLink();
        AccountLoader loader = new(_discoveryCache, CreateClient(authHandler));

        await AccountEndpointsExtensions.DownloadAccountExportAsync(
            Password, _httpContext, loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        authHandler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        authHandler.LastRequest.RequestUri!.ToString().ShouldBe($"{BaseUrl}{ExportHref}");
        authHandler.LastRequestBody!.ShouldContain(Password);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "invalid-password")]
    [InlineData(HttpStatusCode.TooManyRequests, "too-many-requests")]
    [InlineData(HttpStatusCode.NotFound, "failed")]
    [InlineData(HttpStatusCode.Unauthorized, "failed")]
    [InlineData(HttpStatusCode.BadGateway, "failed")]
    public async Task DownloadAccountExportAsync_WhenTheAuthApiRefuses_GoesBackToTheExportPageAndNeverAsksTheTms(
        HttpStatusCode status, string expectedError)
    {
        StubDiscoveryWithAccountLink();
        AccountLoader loader = new(
            _discoveryCache,
            CreateClient(CreateAuthHandler(status, $$"""{ "title": "Refused", "status": {{(int)status}} }""")));
        StubHttpMessageHandler tmsHandler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(CreateContribution(), ApiJsonOptions));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            Password, _httpContext, loader, _discoveryCache, CreateTmsClient(tmsHandler), NullLoggerFactory.Instance, CancellationToken.None);

        RedirectHttpResult redirect = result.ShouldBeOfType<RedirectHttpResult>();
        redirect.Url.ShouldBe($"/account/export?error={expectedError}");
        // A refused password must not leak the other half of the export either.
        tmsHandler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTheAuthLegAnswersAMaintenancePageWithA200_GoesBackToTheExportPageInsteadOfThrowing()
    {
        // The #637 outage with a success status: the proxy's maintenance page arrives as 200 with HTML.
        // That used to throw a JsonException out of the route (#638). It is a failed auth part like any
        // other.
        StubDiscoveryWithAccountLink();
        AccountLoader loader = new(
            _discoveryCache,
            CreateClient(CreateAuthHandler(
                HttpStatusCode.OK,
                "<html><head><title>Maintenance</title></head><body>Back soon</body></html>")));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            Password, _httpContext, loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        RedirectHttpResult redirect = result.ShouldBeOfType<RedirectHttpResult>();
        redirect.Url.ShouldBe("/account/export?error=failed");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenTheAccountDoesNotAdvertiseTheExport_GoesBackWithoutPostingThePassword()
    {
        // No rel means the server does not offer the export to this caller. The route never composes
        // the path itself (#610), and the password stays here.
        StubDiscoveryWithAccountLink();
        StubHttpMessageHandler authHandler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(AccountLoaderTests.CreateEnvelope(links: []), ApiJsonOptions));
        AccountLoader loader = new(_discoveryCache, CreateClient(authHandler));

        IResult result = await AccountEndpointsExtensions.DownloadAccountExportAsync(
            Password, _httpContext, loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        RedirectHttpResult redirect = result.ShouldBeOfType<RedirectHttpResult>();
        redirect.Url.ShouldBe("/account/export?error=unavailable");
        authHandler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenDiscoveryFails_GoesBackToTheExportPage()
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
            Password, _httpContext, loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        RedirectHttpResult redirect = result.ShouldBeOfType<RedirectHttpResult>();
        redirect.Url.ShouldBe("/account/export?error=unavailable");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_OnSuccess_TellsEveryCacheNotToKeepTheFile()
    {
        AccountLoader loader = CreateLoaderReturningExport();

        await AccountEndpointsExtensions.DownloadAccountExportAsync(
            Password, _httpContext, loader, _discoveryCache, CreateTmsClientReturningContribution(), NullLoggerFactory.Instance, CancellationToken.None);

        _httpContext.Response.Headers.CacheControl.ToString().ShouldBe("no-store");
    }

    [Fact]
    public async Task DownloadAccountExportAsync_OnSuccess_LogsWhoTookTheFileAndFromWhere()
    {
        // The auth API sees this server's address. Only this route sees the browser's, so the audit
        // line that answers "from where" is written here (#690).
        using CapturingLoggerProvider provider = new();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        AccountLoader loader = CreateLoaderReturningExport();

        await AccountEndpointsExtensions.DownloadAccountExportAsync(
            Password, _httpContext, loader, _discoveryCache, CreateTmsClientReturningContribution(), loggerFactory, CancellationToken.None);

        CapturingLoggerProvider.LogEntry entry = provider.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldContain(Subject);
        entry.Message.ShouldContain(ClientIp);
        entry.Message.ShouldContain(ClientUserAgent);
        entry.Message.ShouldNotContain(Password);
    }

    [Fact]
    public async Task DownloadAccountExportAsync_WhenThePasswordIsRefused_LogsTheAttempt()
    {
        using CapturingLoggerProvider provider = new();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        StubDiscoveryWithAccountLink();
        AccountLoader loader = new(
            _discoveryCache,
            CreateClient(CreateAuthHandler(HttpStatusCode.BadRequest, """{ "title": "Bad Request", "status": 400 }""")));

        await AccountEndpointsExtensions.DownloadAccountExportAsync(
            Password, _httpContext, loader, _discoveryCache, CreateTmsClientReturningContribution(), loggerFactory, CancellationToken.None);

        CapturingLoggerProvider.LogEntry entry = provider.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain(Subject);
        entry.Message.ShouldContain("invalid-password");
        entry.Message.ShouldContain(ClientIp);
        entry.Message.ShouldContain(ClientUserAgent);
        entry.Message.ShouldNotContain(Password);
    }

    private AccountLoader CreateLoaderReturningExport()
    {
        StubDiscoveryWithAccountLink();
        return new AccountLoader(_discoveryCache, CreateClient(CreateAuthHandler(HttpStatusCode.OK, ExportJson())));
    }

    private static string ExportJson() => JsonSerializer.Serialize(AccountLoaderTests.CreateExport(), ApiJsonOptions);

    /// <summary>
    /// The auth API as the route sees it: a GET of the account resource, which advertises the export,
    /// and a POST of the export that answers with what the test passes in.
    /// </summary>
    private static StubHttpMessageHandler CreateAuthHandler(HttpStatusCode exportStatus, string exportBody)
    {
        string accountJson = JsonSerializer.Serialize(
            AccountLoaderTests.CreateEnvelope(links: [new LinkDto(ExportHref, Rels.ExportAccountData, "POST")]),
            ApiJsonOptions);

        return StubHttpMessageHandler.RespondBy(request => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(accountJson, Encoding.UTF8, "application/json")
            }
            : new HttpResponseMessage(exportStatus)
            {
                Content = new StringContent(exportBody, Encoding.UTF8, "application/json")
            });
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        DefaultHttpContext httpContext = new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Subject)], "test"))
        };
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(ClientIp);
        httpContext.Request.Headers.UserAgent = ClientUserAgent;
        return httpContext;
    }

    private void StubDiscoveryWithAccountLink()
    {
        AuthDiscoveryResponse discovery = new("LotroKoniecDev.AuthSystem")
        {
            Links = [new LinkDto(AccountHref, Rels.Account, "GET")]
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
