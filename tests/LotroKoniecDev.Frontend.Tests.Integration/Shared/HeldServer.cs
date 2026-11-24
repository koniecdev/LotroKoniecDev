using System.Net;

namespace LotroKoniecDev.Frontend.Tests.Integration.Shared;

/// <summary>
/// Holds every answer until the test releases it, so a streamed page sends its first frame while it still
/// waits for its data. It then answers 404, so the page shows its calm notice.
/// </summary>
public sealed class HeldServer : HttpMessageHandler
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource RequestArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _release.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestArrived.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
