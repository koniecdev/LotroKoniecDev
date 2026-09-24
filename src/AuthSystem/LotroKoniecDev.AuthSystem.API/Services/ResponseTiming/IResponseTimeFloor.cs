namespace LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;

/// <summary>
/// Holds back an answer that must not reveal whether an address has an account until a fixed time has
/// passed. The time of the answer then depends on the floor, not on what the branch did (ADR-0059).
/// </summary>
internal interface IResponseTimeFloor
{
    /// <summary>
    /// Starts the clock just before the address lookup. Await <see cref="ResponseTimer.WaitForFloorAsync"/>
    /// before the answer leaves.
    /// </summary>
    ResponseTimer Start(TimeSpan floor);
}
