using LotroKoniecDev.DesktopApp.Presentation.Settings;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Shared;

/// <summary><c>config.json</c> in memory. Set a failure to make the next read, write or set-aside throw like the disk would.</summary>
internal sealed class InMemorySettingsFile : ISettingsFile
{
    public string? Contents { get; set; }

    /// <summary>What <see cref="SetAside"/> moved aside, as the real file moves it to a <c>config.json.broken.</c> name.</summary>
    public string? SetAsideContents { get; private set; }

    public Exception? ReadFailure { get; set; }

    public Exception? WriteFailure { get; set; }

    public Exception? SetAsideFailure { get; set; }

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

    public void SetAside()
    {
        if (SetAsideFailure is not null)
        {
            throw SetAsideFailure;
        }

        if (Contents is null)
        {
            return;
        }

        SetAsideContents = Contents;
        Contents = null;
    }
}
