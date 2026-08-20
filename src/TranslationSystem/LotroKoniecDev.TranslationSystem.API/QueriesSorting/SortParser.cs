namespace LotroKoniecDev.TranslationSystem.API.QueriesSorting;

/// <summary>
/// Reads a <c>?sort=</c> value such as <c>status:desc,fileId:asc</c> into an ordered list of
/// <see cref="SortItem"/>s. The keys are separated by commas and each may end in <c>:asc</c> or
/// <c>:desc</c>, with ascending as the default. Empty parts and parts with no key are skipped, so a
/// malformed value simply means "no sort" instead of an error.
/// </summary>
internal static class SortParser
{
    extension(IEnumerable<SortItem>)
    {
        public static IEnumerable<SortItem> Parse(string sort)
        {
            if (string.IsNullOrWhiteSpace(sort))
            {
                yield break;
            }

            string[] lines = sort.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                string trimmedLine = line.Trim();
                string[] parts = trimmedLine.Split(':', 2);

                string propertyName = parts[0].Trim();
                if (string.IsNullOrEmpty(propertyName))
                {
                    continue;
                }

                SortOperand operand =
                    parts.Length > 1 && parts[1].Trim().Equals("desc", StringComparison.OrdinalIgnoreCase)
                        ? SortOperand.Desc
                        : SortOperand.Asc;
                SortItem sortItem = new(propertyName, operand);
                yield return sortItem;
            }
        }
    }
}
