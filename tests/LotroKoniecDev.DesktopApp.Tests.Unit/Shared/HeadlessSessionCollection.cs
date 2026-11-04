namespace LotroKoniecDev.DesktopApp.Tests.Unit.Shared;

/// <summary>Every test class that needs the UI thread joins this collection, so they share one session.</summary>
[CollectionDefinition(Name)]
public sealed class HeadlessSessionCollection : ICollectionFixture<HeadlessSessionFixture>
{
    public const string Name = "Avalonia headless session";
}
