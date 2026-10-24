using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// So the hundreds of tests that post to these pages do not each wait up to three seconds.
/// <c>ResponseTimeFloorEndpointTests</c> puts the real floor back (ADR-0059).
/// </summary>
internal sealed class NoResponseTimeFloor : IResponseTimeFloor
{
    public Task<T> HoldAsync<T>(TimeSpan floor, Func<Task<T>> work, Func<T, bool>? skipWaitFor = null) => work();

    public Task HoldAsync(TimeSpan floor, Func<Task> work) => work();
}
