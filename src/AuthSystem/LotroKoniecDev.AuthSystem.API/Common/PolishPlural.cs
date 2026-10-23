namespace LotroKoniecDev.AuthSystem.API.Common;

/// <summary>
/// "1 minutę", "22 minuty", "25 minut": the second form follows a number ending in 2, 3 or 4, except 12,
/// 13 and 14 (#1032). The frontend has its own copy (<c>TranslationsPlural</c>).
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
