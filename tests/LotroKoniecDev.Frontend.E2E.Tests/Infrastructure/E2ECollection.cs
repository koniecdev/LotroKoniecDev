namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class E2ECollection : ICollectionFixture<PlaywrightStackFixture>
{
    public const string Name = "Frontend-E2E";
}
