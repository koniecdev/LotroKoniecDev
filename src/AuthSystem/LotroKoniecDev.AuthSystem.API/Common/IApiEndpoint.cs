namespace LotroKoniecDev.AuthSystem.API.Common;

/// <summary>
/// Marks an endpoint that lives under the <c>/api</c> prefix. A plain <see cref="IEndpoint"/> is
/// mapped at the application root instead, as OpenIddict's <c>/connect/*</c> endpoints are.
/// </summary>
internal interface IApiEndpoint : IEndpoint;
