using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LotroKoniecDev.Application.Features.AppUpdating;

/// <summary>
/// The desktop app's own version, <c>X.Y.Z</c> (spec 0016). One parser reads both the version the
/// app was built with and the version in a <c>desktop-vX.Y.Z</c> tag, so the two can never disagree
/// on the form.
/// </summary>
public sealed partial record DesktopVersion(int Major, int Minor, int Patch)
{
    /// <summary>What an unstamped build carries. Such a build never checks for updates.</summary>
    public static readonly DesktopVersion DevelopmentBuild = new(0, 0, 0);

    public bool IsDevelopmentBuild => this == DevelopmentBuild;

    public bool IsNewerThan(DesktopVersion other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch)) > 0;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>
    /// Accepts three plain numbers with no leading zeros and no suffix. Nine digits at most per part,
    /// so every part fits an <see cref="int"/>.
    /// </summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out DesktopVersion? version)
    {
        version = null;
        if (value is null)
        {
            return false;
        }

        Match match = VersionPattern().Match(value);
        if (!match.Success)
        {
            return false;
        }

        version = new DesktopVersion(
            int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].ValueSpan, CultureInfo.InvariantCulture));
        return true;
    }

    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
