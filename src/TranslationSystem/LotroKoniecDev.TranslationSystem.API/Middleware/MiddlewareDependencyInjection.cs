namespace LotroKoniecDev.TranslationSystem.API.Middleware;

public static class MiddlewareDependencyInjection
{
    extension(IApplicationBuilder app)
    {
        public IApplicationBuilder UseRequestContextLogging()
        {
            app.UseMiddleware<RequestContextLoggingMiddleware>();
            return app;
        }

        public IApplicationBuilder UseGlobalNoCache()
        {
            app.UseMiddleware<GlobalNoCacheMiddleware>();
            return app;
        }

        public IApplicationBuilder UseAuthorizationLogging()
        {
            app.UseMiddleware<AuthorizationLoggingMiddleware>();
            return app;
        }
    }
}
