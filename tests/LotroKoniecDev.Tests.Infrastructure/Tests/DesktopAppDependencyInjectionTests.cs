using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.DesktopApp;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// The desktop app's composition root with the real Windows adapters (#41). It lives here because the
/// exe is x86 Windows, like this suite, so no Linux runner can load it.
/// </summary>
public sealed class DesktopAppDependencyInjectionTests
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
    public void BuildServiceProvider_InTheAppScope_ResolvesEveryPatcherHandler(Type handlerInterface)
    {
        // Arrange: validation is on, so building the provider is already a check of the whole graph.
        using ServiceProvider serviceProvider = DesktopAppDependencyInjection.BuildServiceProvider();
        using IServiceScope scope = serviceProvider.CreateScope();

        // Act
        object handler = scope.ServiceProvider.GetRequiredService(handlerInterface);

        // Assert
        handler.ShouldBeAssignableTo(handlerInterface);
    }

    [Fact]
    public void BuildServiceProvider_InTheAppScope_ResolvesTheMainWindowViewModel()
    {
        // Arrange
        using ServiceProvider serviceProvider = DesktopAppDependencyInjection.BuildServiceProvider();
        using IServiceScope scope = serviceProvider.CreateScope();

        // Act
        MainWindowViewModel viewModel = scope.ServiceProvider.GetRequiredService<MainWindowViewModel>();

        // Assert: one view model per app scope, so the progress the handlers report reaches the open window.
        viewModel.ShouldBeSameAs(scope.ServiceProvider.GetRequiredService<MainWindowViewModel>());
    }

    [Fact]
    public void BuildServiceProvider_FromTheRootProvider_RefusesTheScopedViewModel()
    {
        // Arrange
        using ServiceProvider serviceProvider = DesktopAppDependencyInjection.BuildServiceProvider();

        // Act & Assert: ValidateScopes is on (#572), which is why Program resolves from one app-wide scope.
        Should.Throw<InvalidOperationException>(() => serviceProvider.GetRequiredService<MainWindowViewModel>());
    }

    private static bool IsHandlerInterface(Type serviceType) =>
        serviceType.IsGenericType && HandlerInterfaceDefinitions.Contains(serviceType.GetGenericTypeDefinition());
}
