namespace LotroKoniecDev.AuthSystem.API.Extensions;

internal static class EnvironmentsExtensions
{
    public const string DevelopmentName = "Development";
    public const string TestingName = "Testing";
    public const string ProductionName = "Production";
    extension(Environments)
    {
        public static string Development => DevelopmentName;
        public static string Testing => TestingName;
    }
}
