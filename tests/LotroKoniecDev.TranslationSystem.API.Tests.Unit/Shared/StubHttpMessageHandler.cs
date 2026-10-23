using System.Net;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;

/// <summary>
/// Answers every request with a 200 and the content you build.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpContent> _content;

    public StubHttpMessageHandler(Func<HttpContent> content)
    {
        _content = content;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = _content() });
}
