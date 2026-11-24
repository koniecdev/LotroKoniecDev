namespace LotroKoniecDev.Frontend.Infrastructure.Forms;

internal static class EndedSessionFormPostExtensions
{
    extension(IApplicationBuilder app)
    {
        /// <summary>After <c>UseAntiforgery</c>, which records whether the form's token fits.</summary>
        public IApplicationBuilder UseSignInForAFormPostFromAnEndedSession()
        {
            app.UseMiddleware<EndedSessionFormPostMiddleware>();
            return app;
        }
    }
}
