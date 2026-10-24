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
/// The pipeline's per-request decisions: the long timeout for the ~80 MB upload (#208), and a password
/// confirmation sent only once, because each attempt spends one permit of the account's budget (#813).
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
        // A retry after a timeout would spend a second permit while the first is still served (ADR-0053).
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
        // Through the real registration: only the request builder's body type lets the pipeline recognise
        // the request, which a test of MayRetry cannot see. All three POST verbs, because the account pages
        // use all three. The send count is not in the ApiResult.
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
        // The control for the test above: any other body is still retried.
        CountingHttpMessageHandler primary = new(HttpStatusCode.InternalServerError);
        await using ServiceProvider provider = BuildAuthClientProvider(primary);
        IAuthSystemClient client = provider.GetRequiredService<IAuthSystemClient>();

        bool failed = await PostAsync(client, verb, new { id = 1 });

        failed.ShouldBeTrue();
        primary.SendCount.ShouldBe(1 + MaxRetryAttempts);
    }

    private static async Task<bool> PostAsync(IAuthSystemClient client, string verb, object body)
    {
        const string uri = "auth/account/delete";

        return verb switch
        {
            nameof(IAuthSystemClient.PostApiResultAsync) =>
                (await client.PostApiResultAsync(uri, body)).IsFailure,
            nameof(IAuthSystemClient.PostApiResultAsync) + "<T>" =>
                (await client.PostApiResultAsync<AccountDataExportResponse>(uri, body)).IsFailure,
            nameof(IAuthSystemClient.PostForHeadersApiResultAsync) =>
                (await client.PostForHeadersApiResultAsync(uri, body)).IsFailure,
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "Unknown verb")
        };
    }

    /// <summary>Mirrors the pipeline in HttpClientsDependencyInjectionExtensions.</summary>
    private const int MaxRetryAttempts = 2;

    /// <summary>
    /// A later ConfigurePrimaryHttpMessageHandler wins, so only the transport changes.
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
