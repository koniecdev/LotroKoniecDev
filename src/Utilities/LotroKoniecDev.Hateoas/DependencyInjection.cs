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
        /// Call this after <c>AddProblemDetails()</c>, so the default writer handles the Accept types it
        /// supports and the fallback writer is tried last.
        /// <para>
        /// Link factories for one aggregate (<c>IAccountAggregateLinkFactory</c>,
        /// <c>ITranslationAggregateLinkFactory</c>) stay with each service: they know endpoint names and
        /// state-dependent rels, which do not belong in a shared library.
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
