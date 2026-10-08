using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// Makes a test host sign tokens with other audiences than the server picks. A test can then hold a token
/// the server no longer issues: one from before #1023, or a client token that names this API and so
/// reaches the checks behind the audience check. The handler runs before OpenIddict builds any token from
/// the principal.
/// </summary>
internal static class SignInAudienceOverride
{
    public static IServiceCollection OverrideSignInAudiences(
        this IServiceCollection services,
        Func<OpenIddictServerEvents.ProcessSignInContext, bool> when,
        params string[] audiences)
    {
        services.AddOpenIddict().AddServer(options =>
            options.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(handler =>
                handler
                    .UseInlineHandler(context =>
                    {
                        if (when(context))
                        {
                            context.Principal?.SetResources(audiences);
                        }

                        return ValueTask.CompletedTask;
                    })
                    .SetOrder(int.MinValue)));

        return services;
    }
}
