namespace LotroKoniecDev.TranslationSystem.API.QueriesSorting;

/// <summary>
/// Parses a <c>?sort=</c> query string (e.g. <c>status:desc,fileId:asc</c>) into an ordered sequence
/// of <see cref="SortItem"/>s: comma-separated keys, each optionally suffixed with <c>:asc</c>/<c>:desc</c>
/// (default ascending). Empty, whitespace and key-less segments are skipped, so a malformed value
/// degrades to "no sort" rather than throwing.
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
