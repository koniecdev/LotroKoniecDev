namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

internal enum AccountErasureOutcome
{
    Erased,

    /// <summary>
    /// The erasure stopped because the account no longer waits for it: its owner cancelled the deletion,
    /// or another run erased the account first. No run comes back to it, and none has to (#962).
    /// </summary>
    NoLongerWaiting
}
