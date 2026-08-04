using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace LotroKoniecDev.Cli.DependencyInjection;

public sealed class TypeRegistrar : ITypeRegistrar
{
    private readonly IServiceCollection _services;

    public TypeRegistrar(IServiceCollection services)
    {
        _services = services;
    }

    public ITypeResolver Build()
    {
        // DI validation regardless of environment (#572): a captive dependency or an unresolvable
        // constructor fails the CLI at startup instead of mid-command, halfway through a DAT write.
        ServiceProvider provider = _services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        TypeResolver typeResolver = new(provider);
        return typeResolver;
    }

    // Spectre registers the command types (ExportCommand, PatchCommand, LaunchCommand) through this
    // method, and every one of them injects scoped handlers. Registering them as singletons is a
    // captive dependency: the container refuses to build once ValidateScopes is on. Scoped keeps the
    // lifetimes honest — TypeResolver resolves everything from a single process-lifetime scope.
    public void Register(Type service, Type implementation)
    {
        _services.AddScoped(service, implementation);
    }

    public void RegisterInstance(Type service, object implementation)
    {
        _services.AddSingleton(service, implementation);
    }

    public void RegisterLazy(Type service, Func<object> factory)
    {
        _services.AddSingleton(service, _ => factory());
    }
}
