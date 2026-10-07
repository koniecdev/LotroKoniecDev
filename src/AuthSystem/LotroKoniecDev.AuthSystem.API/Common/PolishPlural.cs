namespace LotroKoniecDev.AuthSystem.API.Common;

/// <summary>
/// Picks the Polish noun form that follows a count: "1 minutę", "22 minuty", "25 minut". The second form
/// follows every number that ends in 2, 3 or 4, except numbers that end in 12, 13 or 14 (#1032). The
/// frontend has its own copy of this rule (<c>TranslationsPlural</c> on the translations page).
/// </summary>
internal static class PolishPlural
{
    public static string Pick(int count, string one, string few, string many)
    {
        if (count == 1)
        {
            return one;
        }

        int last = count % 10;
        int lastTwo = count % 100;
        return last is >= 2 and <= 4 && lastTwo is not (>= 12 and <= 14)
            ? few
            : many;
    }
}
