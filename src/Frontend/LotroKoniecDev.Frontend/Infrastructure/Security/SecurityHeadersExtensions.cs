namespace LotroKoniecDev.Frontend.Infrastructure.Security;

internal static class SecurityHeadersExtensions
{
    extension(IApplicationBuilder app)
    {
        public IApplicationBuilder UseSecurityHeaders()
        {
            app.UseMiddleware<SecurityHeadersMiddleware>();
            return app;
        }
    }
}
