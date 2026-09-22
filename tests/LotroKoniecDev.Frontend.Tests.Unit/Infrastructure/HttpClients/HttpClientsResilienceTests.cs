using System.Net.Http.Json;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;

/// <summary>
/// Guards the resilience pipeline's per-request decisions. The timeout one (#208): the exported.txt
/// upload is a multipart POST that must be granted a far wider budget than an ordinary JSON call, or a
/// healthy ~80 MB upload is aborted mid-flight by the tight default while the API is still importing.
/// The retry one (#813): a request that confirms the current password spends one permit of the account's
/// budget per attempt, so the pipeline must send it exactly once.
/// </summary>
public sealed class HttpClientsResilienceTests
{
    [Fact]
    public void ResolveTimeout_ForMultipartUpload_UsesTheWiderUploadBudget()
    {
        using MultipartFormDataContent content = new();
        using HttpRequestMessage request = new(HttpMethod.Post, "api/v1/game-versions/x/import")
        {
            Content = content
        };

        TimeSpan timeout = HttpClientsDependencyInjectionExtensions.ResolveTimeout(request);

        timeout.ShouldBe(HttpClientsDependencyInjectionExtensions.UploadRequestTimeout);
        timeout.ShouldBeGreaterThan(HttpClientsDependencyInjectionExtensions.DefaultRequestTimeout);
    }

    [Fact]
    public void ResolveTimeout_ForJsonRequest_UsesTheTightDefaultBudget()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "api/v1/translations")
        {
            Content = JsonContent.Create(new { id = 1 })
        };

        TimeSpan timeout = HttpClientsDependencyInjectionExtensions.ResolveTimeout(request);

        timeout.ShouldBe(HttpClientsDependencyInjectionExtensions.DefaultRequestTimeout);
    }

    [Fact]
    public void ResolveTimeout_ForRequestWithoutContent_UsesTheTightDefaultBudget()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "api/v1/game-versions");

        TimeSpan timeout = HttpClientsDependencyInjectionExtensions.ResolveTimeout(request);

        timeout.ShouldBe(HttpClientsDependencyInjectionExtensions.DefaultRequestTimeout);
    }

    [Fact]
    public void ResolveTimeout_ForNullRequest_UsesTheTightDefaultBudget()
    {
        TimeSpan timeout = HttpClientsDependencyInjectionExtensions.ResolveTimeout(null);

        timeout.ShouldBe(HttpClientsDependencyInjectionExtensions.DefaultRequestTimeout);
    }

    public static TheoryData<object> PasswordConfirmationRequests => new()
    {
        new DeleteAccountRequest("Correct-Horse-1!"),
        new ChangePasswordRequest("Correct-Horse-1!", "Battery-Staple-2!"),
        new ChangeEmailRequest("frodo@shire.me", "Correct-Horse-1!"),
        new DownloadAccountDataRequest("Correct-Horse-1!")
    };

    [Theory]
    [MemberData(nameof(PasswordConfirmationRequests))]
    public void MayRetry_ForAPasswordConfirmation_IsFalse(object body)
    {
        // A retry after the per-attempt timeout would be a second confirmation against the account's
        // budget, while the auth API still served the first one (ADR-0053).
        using HttpRequestMessage request = new(HttpMethod.Post, "auth/account/delete")
        {
            Content = JsonContent.Create(body, body.GetType())
        };

        bool mayRetry = HttpClientsDependencyInjectionExtensions.MayRetry(request);

        mayRetry.ShouldBeFalse();
    }

    [Fact]
    public void MayRetry_ForMultipartUpload_IsFalse()
    {
        using MultipartFormDataContent content = new();
        using HttpRequestMessage request = new(HttpMethod.Post, "api/v1/game-versions/x/import")
        {
            Content = content
        };

        bool mayRetry = HttpClientsDependencyInjectionExtensions.MayRetry(request);

        mayRetry.ShouldBeFalse();
    }

    [Fact]
    public void MayRetry_ForAPlainJsonRequest_IsTrue()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "api/v1/translations")
        {
            Content = JsonContent.Create(new { id = 1 })
        };

        bool mayRetry = HttpClientsDependencyInjectionExtensions.MayRetry(request);

        mayRetry.ShouldBeTrue();
    }

    [Fact]
    public void MayRetry_ForARequestWithoutContent_IsTrue()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "api/v1/game-versions");

        HttpClientsDependencyInjectionExtensions.MayRetry(request).ShouldBeTrue();
        HttpClientsDependencyInjectionExtensions.MayRetry(null).ShouldBeTrue();
    }
}
