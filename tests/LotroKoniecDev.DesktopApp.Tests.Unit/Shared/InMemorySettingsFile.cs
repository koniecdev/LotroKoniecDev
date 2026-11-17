using LotroKoniecDev.DesktopApp.Presentation.Settings;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Shared;

/// <summary><c>config.json</c> in memory. Set a failure to make the next read or write throw like the disk would.</summary>
internal sealed class InMemorySettingsFile : ISettingsFile
{
    public string? Contents { get; set; }

    public Exception? ReadFailure { get; set; }

    public Exception? WriteFailure { get; set; }

    public bool Exists() => Contents is not null;

    public string Read()
    {
        if (ReadFailure is not null)
        {
            throw ReadFailure;
        }

        return Contents ?? throw new FileNotFoundException("No settings file in memory.");
    }

    public void Write(string contents)
    {
        if (WriteFailure is not null)
        {
            throw WriteFailure;
        }

        Contents = contents;
    }
}
