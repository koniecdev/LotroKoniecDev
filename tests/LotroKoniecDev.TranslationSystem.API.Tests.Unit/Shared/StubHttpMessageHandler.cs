using System.Net;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers every request with a 200 and the content you build,
/// with no network.
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
