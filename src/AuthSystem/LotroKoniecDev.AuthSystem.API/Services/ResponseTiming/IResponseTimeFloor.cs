namespace LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;

/// <summary>
/// For answers that must not reveal whether an address has an account: their time depends on the floor,
/// not on the branch (ADR-0059). The clock starts before the work and the wait covers a throw too, so a
/// caller cannot start it late or skip it on one branch.
/// </summary>
internal interface IResponseTimeFloor
{
    /// <summary>
    /// <paramref name="work"/> starts with the address lookup. An exception also waits, then leaves
    /// unchanged. <paramref name="skipWaitFor"/> names a result that may leave at once, because only a
    /// caller who proved the password can get it.
    /// </summary>
    Task<T> HoldAsync<T>(TimeSpan floor, Func<Task<T>> work, Func<T, bool>? skipWaitFor = null);

    /// <inheritdoc cref="HoldAsync{T}"/>
    Task HoldAsync(TimeSpan floor, Func<Task> work);
}
