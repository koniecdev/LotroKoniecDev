namespace LotroKoniecDev.Primitives.Constants;

public static class TmsConstants
{
    /// <summary>
    /// The root of the production translation server. The patcher finds every endpoint by link
    /// relation in the service document served here (ADR-0041, #611), so this is the only
    /// translation-server address it holds. A run can point at staging or a local server with
    /// <c>--tms-url</c>.
    /// </summary>
    public const string DefaultBaseUrl = "https://tms.lotro-translator.pl/";
}
