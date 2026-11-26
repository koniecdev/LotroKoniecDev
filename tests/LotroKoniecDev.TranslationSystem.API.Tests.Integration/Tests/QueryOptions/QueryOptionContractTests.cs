using System.Reflection;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.QueryOptions;

/// <summary>
/// The house rule for the translation API's query options, as a test (#1478). Every option an endpoint
/// reads from the query string is a property of a request record in TranslationSystem.Contracts, which
/// the endpoint binds with <c>[AsParameters]</c>. A record that builds an address writes every option the
/// API binds from it. ApiExplorer reports what the running host binds, so a plain lambda parameter is
/// caught too, not only a <c>[FromQuery]</c> attribute.
/// </summary>
[Collection("TranslationApi")]
public sealed class QueryOptionContractTests
{
    private const string ToQueryParametersName = nameof(ListTranslationsRequest.ToQueryParameters);

    private static readonly Assembly ContractsAssembly = typeof(ListTranslationsRequest).Assembly;

    /// <summary>
    /// One per record that builds an address, every option away from its default, so none is left out
    /// for being the default.
    /// </summary>
    private static readonly IReadOnlyList<object> RequestsWithEveryOption =
    [
        new ListTranslationsRequest(
            Search: "Stout-axe",
            Status: TranslationStatus.Draft,
            Page: 2,
            PageSize: 25,
            Sort: "gossipId:desc",
            ConflictsOnly: true,
            Match: TranslationSearchMatch.WholeWord,
            Mine: true,
            After: "azo2MjA3NTY5OTI6MQ"),
        new ImportExportedTextsRequest(AllowMassRemoval: true)
    ];

    private readonly TranslationSystemApiFactory _factory;

    [Fact]
    public void EveryQueryOption_IsAPropertyOfARequestRecordInContracts()
    {
        // Act
        List<string> optionsOutsideContracts = QueryOptions()
            .Where(option => DeclaringTypeOf(option)?.Assembly != ContractsAssembly)
            .Select(option => $"{option.Name} on {DeclaringTypeOf(option)?.FullName ?? "an unknown member"}")
            .ToList();

        // Assert
        optionsOutsideContracts.ShouldBeEmpty();
    }

    [Fact]
    public void QueryOptions_FindTheRequestRecordsTheEndpointsBind()
    {
        // The positive control: a scan that finds nothing must not pass the rule above.
        List<Type?> declaringTypes = QueryOptions().Select(DeclaringTypeOf).Distinct().ToList();

        declaringTypes.ShouldContain(typeof(ListTranslationsRequest));
        declaringTypes.ShouldContain(typeof(ImportExportedTextsRequest));
        declaringTypes.ShouldContain(typeof(ListGameVersionsRequest));
    }

    [Fact]
    public void EveryRecordThatBuildsAnAddress_HasACaseInThisClass()
    {
        // Arrange
        List<Type> recordsWithTheMethod = ContractsAssembly.GetExportedTypes()
            .Where(type => type.GetMethod(ToQueryParametersName, Type.EmptyTypes) is not null)
            .ToList();

        // Act
        List<Type> recordsWithACase = RequestsWithEveryOption.Select(request => request.GetType()).ToList();

        // Assert
        recordsWithTheMethod.ShouldNotBeEmpty();
        recordsWithACase.ShouldBe(recordsWithTheMethod, ignoreOrder: true);
    }

    [Theory]
    [MemberData(nameof(RequestsWithEveryOptionSet))]
    public void ARecordThatBuildsAnAddress_WritesEveryOptionTheApiBindsFromIt(object request)
    {
        // Act
        IEnumerable<KeyValuePair<string, string?>> parameters = (IEnumerable<KeyValuePair<string, string?>>)request
            .GetType()
            .GetMethod(ToQueryParametersName, Type.EmptyTypes)!
            .Invoke(request, null)!;
        List<string> written = parameters.Select(parameter => parameter.Key.ToUpperInvariant()).ToList();

        // Assert
        written.ShouldBe(BoundOptionNamesOf(request.GetType()), ignoreOrder: true);
    }

    public static TheoryData<object> RequestsWithEveryOptionSet() => new(RequestsWithEveryOption);

    public QueryOptionContractTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    private List<ApiParameterDescription> QueryOptions()
        => _factory.Services
            .GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .SelectMany(description => description.ParameterDescriptions)
            .Where(parameter => parameter.Source == BindingSource.Query)
            .ToList();

    /// <summary>
    /// The query binds by name, ignoring case, so the names are compared in upper case.
    /// </summary>
    private List<string> BoundOptionNamesOf(Type requestType)
        => QueryOptions()
            .Where(option => DeclaringTypeOf(option) == requestType)
            .Select(option => option.Name.ToUpperInvariant())
            .Distinct()
            .ToList();

    /// <summary>
    /// For an <c>[AsParameters]</c> property the member is the property, so its type is the record; for a
    /// plain lambda parameter it is the compiler's lambda class in the API assembly.
    /// </summary>
    private static Type? DeclaringTypeOf(ApiParameterDescription option)
        => (option.ParameterDescriptor as IParameterInfoParameterDescriptor)?.ParameterInfo.Member.DeclaringType;
}
