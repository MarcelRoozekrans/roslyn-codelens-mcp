using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace RoslynCodeGraph.Tests;

internal static class TestInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }
    }
}
