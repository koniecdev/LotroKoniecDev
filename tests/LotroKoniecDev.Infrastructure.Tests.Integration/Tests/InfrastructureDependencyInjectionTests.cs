using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Application.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Infrastructure.Tests.Integration.Tests;

/// <summary>
/// The cross-platform registration under every patcher handler, on any OS. Only the five Windows
/// ports and the host's progress reporter are stand-ins, so this is also the list a Linux host must
/// fill (ADR-0033, 2026-11-18 amendment; #1322). The real Windows graph is checked in
/// <c>Tests.Infrastructure</c>.
/// </summary>
public sealed class InfrastructureDependencyInjectionTests
{
    private static readonly Type[] HandlerInterfaceDefinitions = [typeof(ICommandHandler<,>), typeof(IQueryHandler<,>)];

    /// <summary>
    /// Every handler Application registers, read from its own registration, so a handler added later is
    /// covered without touching this test.
    /// </summary>
    public static TheoryData<Type> PatcherHandlerInterfaces() =>
        new(new ServiceCollection()
            .AddApplicationServices()
            .Select(descriptor => descriptor.ServiceType)
            .Where(IsHandlerInterface));

    [Fact]
    public void PatcherHandlerInterfaces_FromTheApplicationRegistration_AreNotEmpty()
    {
        // A broken lookup would turn the theory below into zero cases, which xUnit reports as green.
        PatcherHandlerInterfaces().Count.ShouldBeGreaterThan(0);
    }

    [Theory]
    [MemberData(nameof(PatcherHandlerInterfaces))]
    public void AddInfrastructureServices_WithStandInsForTheWindowsPorts_ResolvesEveryPatcherHandler(Type handlerInterface)
    {
        // Arrange: validation is on, so building the provider is already a check of the whole graph.
        using ServiceProvider serviceProvider = BuildServiceProviderWithWindowsStandIns();
        using IServiceScope scope = serviceProvider.CreateScope();

        // Act
        object handler = scope.ServiceProvider.GetRequiredService(handlerInterface);

        // Assert
        handler.ShouldBeAssignableTo(handlerInterface);
    }

    private static bool IsHandlerInterface(Type serviceType) =>
        serviceType.IsGenericType && HandlerInterfaceDefinitions.Contains(serviceType.GetGenericTypeDefinition());

    /// <remarks>
    /// The stand-ins keep the lifetimes of the real registrations, so a scope clash shows up here too.
    /// </remarks>
    private static ServiceProvider BuildServiceProviderWithWindowsStandIns()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddApplicationServices();
        services.AddInfrastructureServices();

        services.AddScoped(_ => Substitute.For<IDatFileHandler>());
        services.AddScoped(_ => Substitute.For<IDatVersionReader>());
        services.AddSingleton(_ => Substitute.For<IDatFileLocator>());
        services.AddSingleton(_ => Substitute.For<IGameLauncher>());
        services.AddSingleton(_ => Substitute.For<IInstallerFileOpener>());
        services.AddScoped(_ => Substitute.For<IProgress<OperationProgress>>());

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }
}
