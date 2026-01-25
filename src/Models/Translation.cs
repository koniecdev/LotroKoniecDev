namespace LotroKoniecDev.Models;

public class Translation
{
    public int FileId { get; set; }
    public int GossipId { get; set; }
    public string Content { get; set; } = string.Empty;
    public int[]? ArgsOrder { get; set; }
    public int[]? ArgsId { get; set; }

    /// <summary>
    /// Rozdziela content na czesci wg separatora
    /// </summary>
    public string[] GetPieces()
    {
        const string separator = "<--DO_NOT_TOUCH!-->";
        return Content.Split(new[] { separator }, StringSplitOptions.None);
    }

    public bool HasArgs => ArgsOrder != null && ArgsOrder.Length > 0;
}
