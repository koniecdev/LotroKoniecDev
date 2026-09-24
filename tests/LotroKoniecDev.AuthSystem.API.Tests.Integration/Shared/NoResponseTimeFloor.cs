using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// The floor this suite runs with, so the hundreds of tests that post to these pages do not each wait up
/// to three seconds. <c>ResponseTimeFloorTests</c> puts the real one back and checks every answer that has
/// to wait (ADR-0059).
/// </summary>
internal sealed class NoResponseTimeFloor : IResponseTimeFloor
{
    public ResponseTimer Start(TimeSpan floor) => new(TimeProvider.System, TimeSpan.Zero);
}
