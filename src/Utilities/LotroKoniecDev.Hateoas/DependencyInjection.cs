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
        /// Registers the cross-cutting HATEOAS infrastructure:
        /// <list type="bullet">
        ///   <item><see cref="ILinkFactory"/> backed by ASP.NET's <c>LinkGenerator</c> and the
        ///   target endpoint's own authorization metadata.</item>
        ///   <item><see cref="IHttpContextAccessor"/> — required by the link factory for absolute URIs.</item>
        ///   <item>A fallback <see cref="IProblemDetailsWriter"/> that serves RFC 7807 for the HATEOAS vendor media type.</item>
        ///   <item>A <see cref="JsonTypeInfo"/> modifier that suppresses empty <c>links</c> arrays from plain-JSON responses.</item>
        /// </list>
        /// <para>
        /// Aggregate-specific link factories (e.g. <c>IAccountAggregateLinkFactory</c>,
        /// <c>ICatAggregateLinkFactory</c>) remain the responsibility of each service —
        /// they encode domain knowledge (endpoint names, state-aware rels) that does not
        /// belong in a cross-cutting library.
        /// </para>
        /// <para>
        /// Must be called <em>after</em> <c>AddProblemDetails()</c> so the default writer
        /// handles its supported Accept types first, with this fallback tried last.
        /// </para>
        /// </summary>
        public IServiceCollection AddHateoasInfrastructure()
        {
            services.AddHttpContextAccessor();

            // Scoped, not singleton: the link factory evaluates the target endpoint's authorization
            // through the scoped IAuthorizationService before emitting a link.
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
