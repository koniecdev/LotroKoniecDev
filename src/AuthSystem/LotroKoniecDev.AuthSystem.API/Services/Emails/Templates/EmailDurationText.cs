using System.Globalization;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails.Templates;

/// <summary>
/// Formats a token lifespan for the Polish locative phrase "wygasa po …". That form does not
/// change with the count above one ("po 2 godzinach", "po 24 godzinach"), so only the singular
/// needs its own case.
/// </summary>
internal static class EmailDurationText
{
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
}
