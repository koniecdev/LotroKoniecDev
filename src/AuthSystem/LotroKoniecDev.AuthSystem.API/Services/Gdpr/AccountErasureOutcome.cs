namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

internal enum AccountErasureOutcome
{
    Erased,

    /// <summary>
    /// The erasure stopped because the account no longer waits for it: its owner cancelled the deletion,
    /// or another run erased the account first. No run comes back to it (#962). When the emergency lock
    /// matched no row and the erasure cannot tell whether its own save landed, it returns this too, after
    /// a warning that says so. After a failed lock the same doubt ends in a failure instead, because the
    /// account may still wait (#980).
    /// </summary>
    NoLongerWaiting
}
