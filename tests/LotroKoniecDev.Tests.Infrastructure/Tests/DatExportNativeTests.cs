using System.Reflection;
using System.Runtime.InteropServices;
using LotroKoniecDev.Infrastructure;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

public sealed class DatExportNativeTests
{
    [Fact]
    public void InfrastructureAssembly_ShouldRestrictNativeDllSearchPaths_ToAssemblyAndSafeDirectories()
    {
        DefaultDllImportSearchPathsAttribute? attribute = typeof(InfrastructureDependencyInjection).Assembly
            .GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();

        attribute.ShouldNotBeNull();
        attribute.Paths.ShouldBe(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories);
    }
}
