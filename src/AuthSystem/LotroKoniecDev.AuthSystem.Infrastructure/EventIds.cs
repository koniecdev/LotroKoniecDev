namespace LotroKoniecDev.AuthSystem.Infrastructure;

/// <summary>
/// Event ID ranges: TranslationSystem 1000-1999, AuthSystem 2000-2999, Shared 3000-3999.
/// </summary>
internal static class EventIds
{
    // Email (3100–3199)
    public const int EmailTransportError = 3100;
    public const int EmailUnexpectedError = 3101;
    public const int EmailAuthenticationError = 3102;
    public const int EmailDisconnectWarning = 3103;

    // Messaging (3200–3299)
    public const int BrokerConnected = 3200;
    public const int BrokerMessagePublished = 3201;
    public const int BrokerTeardownWarning = 3202;
}
