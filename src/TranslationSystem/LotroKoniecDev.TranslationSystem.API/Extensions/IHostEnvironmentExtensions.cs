namespace LotroKoniecDev.TranslationSystem.API.Extensions;

internal static class IHostEnvironmentExtensions
{
    extension(IHostEnvironment hostEnvironment)
    {
        public bool IsTesting()
        {
            ArgumentNullException.ThrowIfNull(hostEnvironment);

            return hostEnvironment.IsEnvironment(Environments.Testing);
        }
    }
}
