using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.Hateoas.ContentNegotiation;
using LotroKoniecDev.Hateoas.ExceptionHandlers;
using LotroKoniecDev.Hateoas.LinkFactories;

namespace LotroKoniecDev.Hateoas;

public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the shared HATEOAS pieces:
        /// <list type="bullet">
        ///   <item><see cref="ILinkFactory"/>, built on ASP.NET's <c>LinkGenerator</c> and on the
        ///   target endpoint's own authorization metadata.</item>
        ///   <item><see cref="IHttpContextAccessor"/>, which the link factory needs for absolute URIs.</item>
        ///   <item>A fallback <see cref="IProblemDetailsWriter"/> that serves RFC 7807 for the vendor media type.</item>
        ///   <item>A <see cref="JsonTypeInfo"/> modifier that hides empty <c>links</c> arrays in plain JSON.</item>
        /// </list>
        /// <para>
        /// Link factories for a single aggregate (<c>IAccountAggregateLinkFactory</c>,
        /// <c>ICatAggregateLinkFactory</c>) stay with each service. They know endpoint names and
        /// state-dependent rels, which do not belong in a shared library.
        /// </para>
        /// <para>
        /// Call this after <c>AddProblemDetails()</c>, so the default writer handles the Accept types it
        /// supports and this fallback is tried last.
        /// </para>
        /// </summary>
        public IServiceCollection AddHateoasInfrastructure()
        {
            services.AddHttpContextAccessor();

            // Scoped, not singleton: before it emits a link, the factory checks the target endpoint's
            // authorization through the scoped IAuthorizationService.
            services.AddScoped<ILinkFactory, LinkFactory>();
            services.AddSingleton<IProblemDetailsWriter, FallbackProblemDetailsWriter>();

            services.ConfigureHttpJsonOptions(jsonOptions =>
            {
                IJsonTypeInfoResolver baseResolver =
                    jsonOptions.SerializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
                jsonOptions.SerializerOptions.TypeInfoResolver =
                    baseResolver.WithAddedModifier(HateoasJsonTypeInfoModifiers.SuppressEmptyLinks);
            });

            return services;
        }
    }
}
