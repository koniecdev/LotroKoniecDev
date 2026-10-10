using System.Globalization;
using LotroKoniecDev.AuthSystem.API.Common;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails.Templates;

/// <summary>
/// Formats a token lifetime for a Polish sentence. The noun form depends on the word before it, so each
/// phrase has its own method.
/// </summary>
internal static class EmailDurationText
{
    /// <summary>
    /// For "wygasa po …". Above one the wording does not change ("po 2 godzinach", "po 24 godzinach"),
    /// so only the singular needs a case of its own.
    /// </summary>
    public static string Describe(TimeSpan lifespan)
    {
        if (lifespan >= TimeSpan.FromDays(2))
        {
            int days = (int)Math.Round(lifespan.TotalDays);
            return $"{days.ToString(CultureInfo.InvariantCulture)} dniach";
        }

        int hours = (int)Math.Round(lifespan.TotalHours);
        return hours == 1
            ? "1 godzinie"
            : $"{hours.ToString(CultureInfo.InvariantCulture)} godzinach";
    }

    /// <summary>
    /// For "działa przez …", which needs other forms than "po …": "przez 1 godzinę", "przez 24 godziny",
    /// "przez 5 godzin", "przez 14 dni" (#1032).
    /// </summary>
    public static string DescribeFor(TimeSpan lifespan)
    {
        if (lifespan >= TimeSpan.FromDays(2))
        {
            int days = (int)Math.Round(lifespan.TotalDays);
            return $"{days.ToString(CultureInfo.InvariantCulture)} dni";
        }

        int hours = (int)Math.Round(lifespan.TotalHours);
        return $"{hours.ToString(CultureInfo.InvariantCulture)} {PolishPlural.Pick(hours, "godzinę", "godziny", "godzin")}";
    }
}
