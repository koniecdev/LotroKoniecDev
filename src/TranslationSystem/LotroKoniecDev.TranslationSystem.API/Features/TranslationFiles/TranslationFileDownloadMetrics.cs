using System.Diagnostics.Metrics;

namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// Shows a change in the download pattern before the hosting bill does (#697). It counts the
/// uncompressed file when the response starts, so it is not egress: a compressed or an aborted download
/// counts in full. No client tag, so no address ever becomes a metric label.
/// </summary>
internal sealed class TranslationFileDownloadMetrics
{
    public const string MeterName = "LotroKoniecDev.TranslationSystem.TranslationFiles";

    private readonly Counter<long> _uncompressedSize;

    public void RecordFullDownload(long uncompressedBytes) => _uncompressedSize.Add(uncompressedBytes);

    public TranslationFileDownloadMetrics(IMeterFactory meterFactory)
    {
        Meter meter = meterFactory.Create(MeterName);
        _uncompressedSize = meter.CreateCounter<long>(
            "lotro.translation_file.download.uncompressed_size",
            unit: "By",
            description: "Uncompressed size of each full translation file download, counted when the response starts.");
    }
}
