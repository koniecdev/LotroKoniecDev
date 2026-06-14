using LotroKoniecDev.Application.Abstractions.Messaging;

namespace LotroKoniecDev.Application.Features.TranslationFileSyncing;

/// <summary>
/// Syncs the local translation file with the TMS distribution endpoint before a launch (spec 0001 Q5):
/// conditional download keyed by the cached ETag, written to <see cref="TranslationFilePath"/>.
/// </summary>
public sealed record SyncTranslationFileCommand(
    string TmsBaseUrl,
    string TranslationFilePath) : ICommand<Result<TranslationFileSyncResponse>>;
