namespace LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;

/// <summary>
/// Makes an answer wait until a fixed time has passed. It is for answers that must not reveal whether an
/// address has an account. Their time then depends on the floor, not on what the branch did (ADR-0059).
/// </summary>
internal interface IResponseTimeFloor
{
    /// <summary>
    /// Starts the clock just before the address lookup. Await <see cref="ResponseTimer.WaitForFloorAsync"/>
    /// before the answer leaves.
    /// </summary>
    ResponseTimer Start(TimeSpan floor);
}
