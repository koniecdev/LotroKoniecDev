using System.Net;
using System.Net.Http.Json;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
    }

    [Fact]
    public void MayRetry_ForNullRequest_IsTrue()
    {
        HttpClientsDependencyInjectionExtensions.MayRetry(null).ShouldBeTrue();
    }

    public static TheoryData<string> PostVerbs => new()
    {
        nameof(IAuthSystemClient.PostApiResultAsync),
        nameof(IAuthSystemClient.PostApiResultAsync) + "<T>",
        nameof(IAuthSystemClient.PostForHeadersApiResultAsync)
    };

    [Theory]
    [MemberData(nameof(PostVerbs))]
    public async Task AuthSystemClient_ForAPasswordConfirmation_SendsTheRequestExactlyOnce(string verb)
    {
        // Through the real registration: the typed client, its request builder and the resilience
        // pipeline as Program.cs wires them. The builder passes the body's runtime type to
        // JsonContent.Create, and that is the only thing that lets the pipeline recognise the request;
        // a seam-level test of MayRetry cannot see that link. All three POST verbs are driven, because
        // the account pages use all three (delete goes through the headers one). The send count is a
        // side effect the ApiResult does not show, which is why it is asserted here.
        CountingHttpMessageHandler primary = new(HttpStatusCode.InternalServerError);
        await using ServiceProvider provider = BuildAuthClientProvider(primary);
        IAuthSystemClient client = provider.GetRequiredService<IAuthSystemClient>();

        bool failed = await PostAsync(client, verb, new DeleteAccountRequest("Correct-Horse-1!"));

        failed.ShouldBeTrue();
        primary.SendCount.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(PostVerbs))]
    public async Task AuthSystemClient_ForAPlainJsonBody_RetriesTheServerError(string verb)
    {
        // The control for the test above: with the same pipeline, a body that is not a password
        // confirmation is still retried, so the single send there is the marker's doing and not a
        // pipeline that never retried at all.
        CountingHttpMessageHandler primary = new(HttpStatusCode.InternalServerError);
        await using ServiceProvider provider = BuildAuthClientProvider(primary);
        IAuthSystemClient client = provider.GetRequiredService<IAuthSystemClient>();

        bool failed = await PostAsync(client, verb, new { id = 1 });

        failed.ShouldBeTrue();
        primary.SendCount.ShouldBe(1 + MaxRetryAttempts);
    }

    /// <summary>Sends through the named verb and reports whether the result was a failure.</summary>
    private static async Task<bool> PostAsync(IAuthSystemClient client, string verb, object body)
    {
        const string uri = "auth/account/delete";

        return verb switch
        {
            nameof(IAuthSystemClient.PostApiResultAsync) =>
                (await client.PostApiResultAsync(uri, body)).IsFailure,
            nameof(IAuthSystemClient.PostApiResultAsync) + "<T>" =>
                (await client.PostApiResultAsync<DeleteAccountRequest>(uri, body)).IsFailure,
            nameof(IAuthSystemClient.PostForHeadersApiResultAsync) =>
                (await client.PostForHeadersApiResultAsync(uri, body)).IsFailure,
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "Unknown verb")
        };
    }

    /// <summary>Mirrors the pipeline in HttpClientsDependencyInjectionExtensions: two retries after the first attempt.</summary>
    private const int MaxRetryAttempts = 2;

    /// <summary>
    /// The frontend's own client registration, with the socket handler swapped for a counting stub. A
    /// later ConfigurePrimaryHttpMessageHandler on the same named client wins, so the resilience
    /// pipeline and the delegating handler stay exactly as Program.cs wires them.
    /// </summary>
    private static ServiceProvider BuildAuthClientProvider(HttpMessageHandler primary)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{AuthSystemSettings.ConfigurationSection}:BaseUrl"] = "https://auth.lotro.test/"
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.Configure<AuthSystemSettings>(configuration.GetSection(AuthSystemSettings.ConfigurationSection));
        services.AddHttpClients();
        services.AddHttpClient<IAuthSystemClient, AuthSystemClient>()
            .ConfigurePrimaryHttpMessageHandler(() => primary);

        return services.BuildServiceProvider();
    }

    private sealed class CountingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private int _sendCount;

        public CountingHttpMessageHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        public int SendCount => _sendCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sendCount);
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(string.Empty)
            });
        }
    }
}
