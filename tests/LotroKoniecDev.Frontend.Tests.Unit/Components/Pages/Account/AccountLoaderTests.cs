using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.Frontend.Components.Pages.Account;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Account;

/// <summary>
/// Drives the account loader end to end over a stubbed HTTP handler: discovery finds the
/// <c>export-account-data</c> link, the export GET follows the href it was given, and the writes,
/// scheduling a deletion and changing the password, post the exact request bodies to the hrefs the
/// response offered. Nothing is decided here from role claims.
/// </summary>
public sealed class AccountLoaderTests
{
    private const string BaseUrl = "https://localhost:5003/";
    private const string ExportHref = "auth/account/data-export";

    // The same JSON options the Frontend's HTTP layer uses (HttpClientApiExtensions), so the stub
    // body deserializes through the exact same contract the loader relies on.
    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IDiscoveryCache _discoveryCache = Substitute.For<IDiscoveryCache>();

    [Fact]
    public async Task LoadExportAsync_WhenDiscoveryFails_PassesTheProblemThrough()
    {
        _discoveryCache.GetAuthSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<AuthDiscoveryResponse>(new ProblemDetails
            {
                Title = "Usługa chwilowo niedostępna",
                Status = 503
            }));
        AccountLoader loader = CreateLoader(StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "{}"), out _);

        ApiResult<AccountDataExportResponse> result = await loader.LoadExportAsync();

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(503);
    }

    [Fact]
    public async Task LoadExportAsync_WhenExportLinkMissing_ReturnsForbiddenWithoutCallingTheApi()
    {
        StubDiscovery(links: []);
        AccountLoader loader = CreateLoader(
            StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "{}"),
            out StubHttpMessageHandler handler);

        ApiResult<AccountDataExportResponse> result = await loader.LoadExportAsync();

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(403);
        handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task LoadExportAsync_WhenLinkAdvertised_GetsTheAdvertisedHrefAndReturnsTheEnvelope()
    {
        StubDiscovery(links: [new LinkDto(ExportHref, Rels.ExportAccountData, "GET")]);
        AccountDataExportResponse envelope = CreateEnvelope();
        AccountLoader loader = CreateLoader(
            StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, JsonSerializer.Serialize(envelope, ApiJsonOptions)),
            out StubHttpMessageHandler handler);

        ApiResult<AccountDataExportResponse> result = await loader.LoadExportAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value.AuthData.Username.ShouldBe("frodo");
        result.Value.AuthData.Email.ShouldBe("frodo@shire.me");
        result.Value.AuthData.Roles.ShouldBe(["Translator"]);
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest.RequestUri!.ToString().ShouldBe($"{BaseUrl}{ExportHref}");
    }

    [Fact]
    public async Task ScheduleDeletionAsync_PostsThePasswordToTheGivenHrefAndReturnsTheHeaders()
    {
        AccountLoader loader = CreateLoader(
            StubHttpMessageHandler.RespondWithHeaders(
                HttpStatusCode.NoContent,
                new Dictionary<string, string>
                {
                    ["X-Deletion-Finalizes-At"] = "2026-07-25T10:00:00.0000000+00:00"
                }),
            out StubHttpMessageHandler handler);

        ApiResult<ApiResponseHeaders> result = await loader.ScheduleDeletionAsync(
            "auth/account/delete",
            "S3cret!Password");

        result.IsSuccess.ShouldBeTrue();
        result.Value.GetValueOrDefault("X-Deletion-Finalizes-At")
            .ShouldBe("2026-07-25T10:00:00.0000000+00:00");
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().ShouldBe($"{BaseUrl}auth/account/delete");
        handler.LastRequestBody.ShouldNotBeNull();
        handler.LastRequestBody!.ShouldContain("S3cret!Password");
    }

    [Fact]
    public async Task ScheduleDeletionAsync_WhenPasswordRejected_PassesTheProblemThrough()
    {
        AccountLoader loader = CreateLoader(
            StubHttpMessageHandler.RespondWith(
                HttpStatusCode.UnprocessableEntity,
                """{ "title": "Nieprawidłowe hasło", "status": 422 }"""),
            out _);

        ApiResult<ApiResponseHeaders> result = await loader.ScheduleDeletionAsync(
            "auth/account/delete",
            "wrong-password");

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(422);
    }

    [Fact]
    public async Task ChangePasswordAsync_PostsBothPasswordsToTheGivenHref()
    {
        AccountLoader loader = CreateLoader(
            StubHttpMessageHandler.RespondWith(HttpStatusCode.NoContent, string.Empty),
            out StubHttpMessageHandler handler);

        ApiResult result = await loader.ChangePasswordAsync(
            "auth/change-password",
            "OldP@ss1",
            "NewP@ss2");

        result.IsSuccess.ShouldBeTrue();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().ShouldBe($"{BaseUrl}auth/change-password");
        handler.LastRequestBody.ShouldNotBeNull();
        handler.LastRequestBody!.ShouldContain("OldP@ss1");
        handler.LastRequestBody.ShouldContain("NewP@ss2");
    }

    private void StubDiscovery(List<LinkDto> links)
    {
        AuthDiscoveryResponse discovery = new("LotroKoniecDev.AuthSystem") { Links = links };
        _discoveryCache.GetAuthSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(discovery));
    }

    private AccountLoader CreateLoader(StubHttpMessageHandler handler, out StubHttpMessageHandler captured)
    {
        captured = handler;
        HttpClient httpClient = new(handler)
        {
            BaseAddress = new Uri(BaseUrl)
        };
        return new AccountLoader(_discoveryCache, new AuthSystemClient(httpClient));
    }

    internal static AccountDataExportResponse CreateEnvelope(
        IReadOnlyList<string>? roles = null,
        DateTimeOffset? deletionScheduledAt = null,
        List<LinkDto>? links = null,
        bool termsOfServiceAccepted = true)
    {
        return new AccountDataExportResponse(
            new AuthDataExportDto(
                Guid.NewGuid(),
                "frodo",
                "frodo@shire.me",
                PhoneNumber: null,
                EmailConfirmed: true,
                roles ?? ["Translator"],
                DataProcessingConsentGiven: true,
                new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
                PrivacyPolicyAccepted: true,
                new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
                termsOfServiceAccepted,
                termsOfServiceAccepted ? new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero) : null,
                deletionScheduledAt),
            IsComplete: true)
        {
            Links = links ?? []
        };
    }
}
