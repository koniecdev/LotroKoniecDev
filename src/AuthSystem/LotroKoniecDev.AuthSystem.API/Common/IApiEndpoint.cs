namespace LotroKoniecDev.AuthSystem.API.Common;

/// <summary>
/// Marks an endpoint that goes into the group with the general rate limit. A plain
/// <see cref="IEndpoint"/>, such as OpenIddict's <c>/connect/*</c> endpoints, goes into the group with
/// the stricter brute-force limit instead. Both groups sit at the application root.
/// </summary>
internal interface IApiEndpoint : IEndpoint;
