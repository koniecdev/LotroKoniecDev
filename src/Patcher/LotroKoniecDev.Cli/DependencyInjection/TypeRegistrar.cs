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
        // DI validation runs in every environment (#572). A captive dependency or a constructor that
        // cannot be resolved fails the CLI at startup, not halfway through a DAT write.
        ServiceProvider provider = _services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        TypeResolver typeResolver = new(provider);
        return typeResolver;
    }

    // Spectre registers the command types (ExportCommand, PatchCommand, LaunchCommand) here, and all
    // of them inject scoped handlers. Registering them as singletons would be a captive dependency,
    // and the container refuses to build that once ValidateScopes is on. Scoped is correct, because
    // TypeResolver resolves everything from one scope that lives as long as the process.
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
