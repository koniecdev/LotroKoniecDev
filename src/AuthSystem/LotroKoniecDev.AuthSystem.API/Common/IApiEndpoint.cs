namespace LotroKoniecDev.AuthSystem.API.Common;

/// <summary>
/// Marker for endpoints that live under the <c>/api</c> prefix. Plain <see cref="IEndpoint"/>
/// implementations are mapped at the application root (e.g. OpenIddict's <c>/connect/*</c>).
/// </summary>
internal interface IApiEndpoint : IEndpoint;
