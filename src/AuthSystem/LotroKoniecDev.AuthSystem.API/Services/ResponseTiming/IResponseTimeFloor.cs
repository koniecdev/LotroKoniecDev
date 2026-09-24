namespace LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;

/// <summary>
/// Makes an answer wait until a fixed time has passed. It is for answers that must not reveal whether an
/// address has an account. Their time then depends on the floor, not on what the branch did (ADR-0059).
/// The clock starts before the work and the wait also covers a throw, so a caller cannot start it late or
/// skip it on one branch.
/// </summary>
internal interface IResponseTimeFloor
{
    /// <summary>
    /// Runs <paramref name="work"/>, which starts with the address lookup, and returns its result no
    /// sooner than <paramref name="floor"/> after it started. An exception from the work also waits, then
    /// leaves unchanged. <paramref name="skipWaitFor"/> names a result that may leave at once, because only
    /// a caller who proved the password can get it.
    /// </summary>
    Task<T> HoldAsync<T>(TimeSpan floor, Func<Task<T>> work, Func<T, bool>? skipWaitFor = null);

    /// <inheritdoc cref="HoldAsync{T}"/>
    Task HoldAsync(TimeSpan floor, Func<Task> work);
}
