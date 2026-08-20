using LotroKoniecDev.Application.Abstractions.Messaging;

namespace LotroKoniecDev.Application.Features.TranslationFileSyncing;

/// <summary>
/// Brings the local translation file up to date from the TMS before a launch (spec 0001 Q5). It sends
/// the cached ETag, so only a changed file is downloaded, and writes it to
/// <see cref="TranslationFilePath"/>.
/// </summary>
public sealed record SyncTranslationFileCommand(
    string TmsBaseUrl,
    string TranslationFilePath) : ICommand<Result<TranslationFileSyncResponse>>;
