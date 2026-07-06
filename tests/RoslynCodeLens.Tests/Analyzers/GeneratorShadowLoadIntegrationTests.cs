using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynCodeLens.Analyzers;

namespace RoslynCodeLens.Tests.Analyzers;

/// <summary>
/// End-to-end proof (no MSBuild) that a source generator loaded through
/// <see cref="ShadowCopyAnalyzerAssemblyLoader"/> actually runs and produces symbols, while its
/// original DLL on disk stays unlocked (issue #254). Uses Roslyn's real
/// <see cref="AnalyzerFileReference"/> + <see cref="CSharpGeneratorDriver"/> generator-loading path.
/// </summary>
public class GeneratorShadowLoadIntegrationTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "rcl-gen-test", Guid.NewGuid().ToString("N"));

    public GeneratorShadowLoadIntegrationTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ } }

    // Reference set that lets an incremental generator compile: every trusted-platform assembly
    // (framework) plus the Roslyn assemblies the test process already has loaded.
    private static IReadOnlyList<MetadataReference> GeneratorReferences()
    {
        var refs = new List<MetadataReference>();
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in tpa)
            if (p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                refs.Add(MetadataReference.CreateFromFile(p));
        // Ensure the Roslyn generator API assemblies are referenced even if not in the TPA set.
        refs.Add(MetadataReference.CreateFromFile(typeof(IIncrementalGenerator).Assembly.Location));  // Microsoft.CodeAnalysis
        refs.Add(MetadataReference.CreateFromFile(typeof(CSharpSyntaxTree).Assembly.Location));        // Microsoft.CodeAnalysis.CSharp
        return refs;
    }

    private string BuildGeneratorDll()
    {
        const string generatorSource = """
            using Microsoft.CodeAnalysis;
            [Generator]
            public sealed class HelloGenerator : IIncrementalGenerator
            {
                public void Initialize(IncrementalGeneratorInitializationContext context)
                {
                    context.RegisterPostInitializationOutput(ctx =>
                        ctx.AddSource("Hello.g.cs",
                            "namespace Generated { public static class Hello { public const string Message = \"hi\"; } }"));
                }
            }
            """;

        var compilation = CSharpCompilation.Create(
            "HelloGen",
            new[] { CSharpSyntaxTree.ParseText(generatorSource) },
            GeneratorReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var dllPath = Path.Combine(_tmp, "HelloGen.dll");
        var emit = compilation.Emit(dllPath);
        Assert.True(emit.Success,
            "generator failed to compile:\n" + string.Join("\n", emit.Diagnostics));
        return dllPath;
    }

    [Fact]
    public void ShadowLoadedGenerator_ProducesSymbol_AndLeavesDllUnlocked()
    {
        var genDll = BuildGeneratorDll();

        using var cache = new SharedAnalyzerAssemblyCache();
        using var loader = new ShadowCopyAnalyzerAssemblyLoader(cache);
        loader.AddDependencyLocation(genDll);

        var analyzerRef = new AnalyzerFileReference(genDll, loader);
        var generators = analyzerRef.GetGenerators(LanguageNames.CSharp);
        Assert.NotEmpty(generators);   // generator was loaded through the shadow loader

        var user = CSharpCompilation.Create(
            "User",
            new[] { CSharpSyntaxTree.ParseText("public class C { }") },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        CSharpGeneratorDriver
            .Create(generators)
            .RunGeneratorsAndUpdateCompilation(user, out var output, out _);

        Assert.NotNull(output.GetTypeByMetadataName("Generated.Hello"));   // semantic fidelity through shadow load

        // The #254 property, proven end-to-end: the original generator DLL is not locked.
        Assert.Null(Record.Exception(() => File.Delete(genDll)));
    }
}
