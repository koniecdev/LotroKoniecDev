namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

/// <summary>
/// What one <see cref="IErasedAccountReconciler.ReconcileAsync"/> run changed.
/// </summary>
internal sealed record ErasedAccountReconciliation(int MessagesScrubbed, int ErasuresAnnounced);
