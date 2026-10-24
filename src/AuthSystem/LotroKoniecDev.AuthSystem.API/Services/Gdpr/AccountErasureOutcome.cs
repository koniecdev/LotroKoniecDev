namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

internal enum AccountErasureOutcome
{
    Erased,

    /// <summary>
    /// The account no longer waits: its owner cancelled, or another run erased it first. No run comes back
    /// to it (#962). Also returned, after a warning, when the lock matched no row and the run cannot tell
    /// whether its own save landed. After a failed lock that doubt ends in a failure instead (#980).
    /// </summary>
    NoLongerWaiting
}
