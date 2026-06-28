using System.Net.Http.Json;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;

/// <summary>
/// Guards the resilience pipeline's per-request timeout decision (#208): the exported.txt upload is a
/// multipart POST that must be granted a far wider budget than an ordinary JSON call, or a healthy
/// ~80 MB upload is aborted mid-flight by the tight default while the API is still importing.
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
}
