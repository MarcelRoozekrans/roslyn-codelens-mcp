using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class GenerateTestSkeletonLogic
{
    public static GenerateTestSkeletonResult Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        string symbol,
        string? framework = null)
    {
        var symbols = resolver.FindSymbols(symbol);
        if (symbols.Count == 0)
            throw new McpToolException(
                ToolErrorCode.SymbolNotFound,
                $"Symbol not found: {symbol}",
                new { symbol });

        var first = symbols[0];
        INamedTypeSymbol targetType;
        List<IMethodSymbol> targetMethods;
        switch (first)
        {
            case INamedTypeSymbol type:
                targetType = type;
                targetMethods = EnumerateEligibleMethods(type).ToList();
                break;
            case IMethodSymbol method:
                targetType = method.ContainingType;
                targetMethods = new List<IMethodSymbol> { method };
                break;
            default:
                throw new McpToolException(
                    ToolErrorCode.InvalidArgument,
                    $"Symbol must be a type or method, got {first.Kind}: {symbol}",
                    new { symbol, kind = first.Kind.ToString() });
        }

        var sourceTree = targetType.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree;
        if (sourceTree is not null && GeneratedCodeDetector.IsGenerated(sourceTree))
            throw new McpToolException(
                ToolErrorCode.InvalidArgument,
                $"Symbol '{symbol}' is in generated code; refusing to generate a test skeleton.",
                new { symbol });

        var fw = ResolveFramework(loaded, framework);
        var todoNotes = new List<string>();

        var className = $"{targetType.Name}Tests";
        var ns = $"{targetType.ContainingNamespace.ToDisplayString()}.Tests";

        var compilation = FindCompilationForType(loaded, targetType);

        var code = BuildClass(targetType, targetMethods, className, ns, fw, todoNotes, compilation);

        var suggestedPath = SuggestFilePath(loaded, targetType, todoNotes);

        return new GenerateTestSkeletonResult(
            Framework: fw.ToString(),
            SuggestedFilePath: suggestedPath,
            ClassName: className,
            Code: code,
            TodoNotes: todoNotes);
    }

    private static IEnumerable<IMethodSymbol> EnumerateEligibleMethods(INamedTypeSymbol type)
    {
        foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (m.MethodKind != MethodKind.Ordinary) continue;
            if (m.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected))
                continue;
            yield return m;
        }
    }

    private static TestFramework ResolveFramework(LoadedSolution loaded, string? overrideName)
    {
        if (overrideName is not null)
        {
            return overrideName.ToLowerInvariant() switch
            {
                "xunit" => TestFramework.XUnit,
                "nunit" => TestFramework.NUnit,
                "mstest" => TestFramework.MSTest,
                _ => throw new McpToolException(
                    ToolErrorCode.InvalidArgument,
                    $"Unknown framework override '{overrideName}'. Use xunit, nunit, or mstest.",
                    new { framework = overrideName }),
            };
        }

        var counts = new Dictionary<TestFramework, int>();
        foreach (var p in loaded.Solution.Projects)
        {
            var detected = TestFrameworkDetector.DetectFramework(p);
            if (detected is null) continue;
            counts.TryGetValue(detected.Value, out var n);
            counts[detected.Value] = n + 1;
        }

        if (counts.Count == 0) return TestFramework.XUnit;

        TestFramework best = TestFramework.XUnit;
        int bestCount = -1;
        foreach (var kv in counts)
        {
            if (kv.Value > bestCount || (kv.Value == bestCount && kv.Key < best))
            {
                best = kv.Key;
                bestCount = kv.Value;
            }
        }
        return best;
    }

    private static Compilation? FindCompilationForType(LoadedSolution loaded, INamedTypeSymbol targetType)
    {
        // Match by source location: the compilation whose syntax trees include
        // the type's declaration is the one whose semantic model can interpret
        // descendant nodes of that declaration.
        var sourceLocation = targetType.Locations.FirstOrDefault(l => l.IsInSource);
        var sourceTree = sourceLocation?.SourceTree;
        if (sourceTree is not null)
        {
            foreach (var c in loaded.Compilations.Values)
            {
                if (c.SyntaxTrees.Contains(sourceTree))
                    return c;
            }
        }

        // Fallback: assembly-symbol equality (works when targetType came from this compilation).
        foreach (var c in loaded.Compilations.Values)
        {
            if (SymbolEqualityComparer.Default.Equals(c.Assembly, targetType.ContainingAssembly))
                return c;
        }
        return null;
    }

    private static string BuildClass(
        INamedTypeSymbol targetType,
        IReadOnlyList<IMethodSymbol> methods,
        string className,
        string ns,
        TestFramework fw,
        List<string> todoNotes,
        Compilation? compilation)
    {
        var sb = new StringBuilder();

        // Usings
        var prodNs = targetType.ContainingNamespace.ToDisplayString();
        var hasAsync = methods.Any(ReturnsTask);

        sb.AppendLine($"using {prodNs};");
        if (hasAsync) sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine(FrameworkUsing(fw));
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"public class {className}");
        sb.AppendLine("{");

        if (targetType.IsAbstract)
            todoNotes.Add($"{targetType.Name} is abstract — instantiate via a derived test fixture.");

        if (methods.Count == 0)
        {
            sb.AppendLine("    // TODO: no public methods detected on " + targetType.Name);
            todoNotes.Add($"{targetType.Name} has no eligible public methods.");
        }

        for (int i = 0; i < methods.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            EmitMethodStub(sb, targetType, methods[i], fw, todoNotes, compilation);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string FrameworkUsing(TestFramework fw) => fw switch
    {
        TestFramework.XUnit => "using Xunit;",
        TestFramework.NUnit => "using NUnit.Framework;",
        TestFramework.MSTest => "using Microsoft.VisualStudio.TestTools.UnitTesting;",
        _ => "",
    };

    private static void EmitMethodStub(
        StringBuilder sb,
        INamedTypeSymbol targetType,
        IMethodSymbol method,
        TestFramework fw,
        List<string> todoNotes,
        Compilation? compilation)
    {
        var isAsync = ReturnsTask(method);
        var hasParams = method.Parameters.Length > 0;
        var allPrimitive = hasParams && method.Parameters.All(IsPrimitiveParam);

        if (allPrimitive && !isAsync)
        {
            EmitTheoryStub(sb, targetType, method, fw, todoNotes);
        }
        else
        {
            EmitHappyPathFact(sb, targetType, method, fw, isAsync, todoNotes);
        }

        EmitThrowStubs(sb, targetType, method, fw, isAsync, compilation, todoNotes);
    }

    private static void EmitThrowStubs(
        StringBuilder sb,
        INamedTypeSymbol targetType,
        IMethodSymbol method,
        TestFramework fw,
        bool isAsync,
        Compilation? compilation,
        List<string> todoNotes)
    {
        if (compilation is null) return;

        var thrown = CollectThrownExceptionTypes(method, compilation);
        if (thrown.Count == 0) return;

        var factAttr = fw switch
        {
            TestFramework.XUnit => "[Fact]",
            TestFramework.NUnit => "[Test]",
            TestFramework.MSTest => "[TestMethod]",
            _ => "[Fact]",
        };

        foreach (var ex in thrown)
        {
            sb.AppendLine();
            sb.AppendLine($"    {factAttr}");
            var asyncReturn = isAsync ? "async Task" : "void";
            sb.AppendLine($"    public {asyncReturn} {method.Name}_Throws{ex}()");
            sb.AppendLine("    {");

            var argList = string.Join(", ", method.Parameters.Select(ThrowStubArg));
            string callExpr;
            if (method.IsStatic)
            {
                callExpr = $"() => {targetType.Name}.{method.Name}({argList})";
            }
            else
            {
                sb.AppendLine($"        var sut = {SutCreation(targetType, todoNotes)};");
                callExpr = $"() => sut.{method.Name}({argList})";
            }

            if (isAsync)
                sb.AppendLine($"        await Assert.ThrowsAsync<{ex}>({callExpr});");
            else
                sb.AppendLine($"        Assert.Throws<{ex}>({callExpr});");

            sb.AppendLine("    }");
        }
    }

    private static IReadOnlyList<string> CollectThrownExceptionTypes(IMethodSymbol method, Compilation compilation)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();

        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            var declNode = syntaxRef.GetSyntax();
            if (declNode.SyntaxTree is null) continue;

            var sm = compilation.GetSemanticModel(declNode.SyntaxTree);

            foreach (var n in declNode.DescendantNodes())
            {
            ObjectCreationExpressionSyntax? ctor = n switch
            {
                ThrowStatementSyntax t => t.Expression as ObjectCreationExpressionSyntax,
                ThrowExpressionSyntax t => t.Expression as ObjectCreationExpressionSyntax,
                _ => null,
            };
            if (ctor is null) continue;

            if (sm.GetTypeInfo(ctor).Type is not INamedTypeSymbol typeSymbol) continue;

            var name = typeSymbol.Name;
            if (seen.Add(name))
                ordered.Add(name);
            }
        }

        return ordered;
    }

    private static void EmitHappyPathFact(
        StringBuilder sb,
        INamedTypeSymbol targetType,
        IMethodSymbol method,
        TestFramework fw,
        bool isAsync,
        List<string> todoNotes)
    {
        var factAttr = fw switch
        {
            TestFramework.XUnit => "[Fact]",
            TestFramework.NUnit => "[Test]",
            TestFramework.MSTest => "[TestMethod]",
            _ => "[Fact]",
        };

        var returnType = isAsync ? "async Task" : "void";
        var awaitable = isAsync ? "await " : "";

        sb.AppendLine($"    {factAttr}");
        sb.AppendLine($"    public {returnType} {method.Name}_HappyPath()");
        sb.AppendLine("    {");

        if (method.IsStatic)
        {
            sb.AppendLine($"        // TODO: arrange inputs");
            sb.AppendLine($"        {awaitable}{targetType.Name}.{method.Name}();");
        }
        else
        {
            sb.AppendLine($"        var sut = {SutCreation(targetType, todoNotes)};");
            sb.AppendLine($"        {awaitable}sut.{method.Name}();");
        }

        sb.AppendLine("        // TODO: assert");
        sb.AppendLine("    }");
    }

    private static string SutCreation(INamedTypeSymbol type, List<string> todoNotes)
    {
        if (type.IsStatic) return ""; // never used, all-static path
        var ctor = type.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public)
            .OrderBy(c => c.Parameters.Length)
            .FirstOrDefault();

        if (ctor is null || ctor.Parameters.Length == 0)
            return $"new {type.Name}()";

        foreach (var p in ctor.Parameters)
            todoNotes.Add($"{type.Name} constructor needs {p.Type.ToDisplayString()} {p.Name} — wire mock or instance.");

        return $"new {type.Name}(/* TODO: dependencies */)";
    }

    private static bool IsPrimitiveParam(IParameterSymbol p)
    {
        var t = p.Type;
        if (t.SpecialType is
            SpecialType.System_String or
            SpecialType.System_Int32 or SpecialType.System_Int64 or
            SpecialType.System_Double or SpecialType.System_Single or
            SpecialType.System_Boolean or SpecialType.System_Char or
            SpecialType.System_Byte) return true;
        return t.TypeKind == TypeKind.Enum;
    }

    private static string DefaultLiteral(IParameterSymbol p) => p.Type.SpecialType switch
    {
        SpecialType.System_String => "\"\"",
        SpecialType.System_Boolean => "false",
        SpecialType.System_Char => "'a'",
        _ => "0",
    };

    private static string ThrowStubArg(IParameterSymbol p) =>
        IsPrimitiveParam(p) ? DefaultLiteral(p) : "default!";

    private static void EmitTheoryStub(
        StringBuilder sb,
        INamedTypeSymbol targetType,
        IMethodSymbol method,
        TestFramework fw,
        List<string> todoNotes)
    {
        var literals = string.Join(", ", method.Parameters.Select(DefaultLiteral));

        if (fw == TestFramework.XUnit)
        {
            sb.AppendLine("    [Theory]");
            sb.AppendLine($"    [InlineData({literals})]");
        }
        else if (fw == TestFramework.NUnit)
        {
            sb.AppendLine($"    [TestCase({literals})]");
        }
        else
        {
            sb.AppendLine("    [DataTestMethod]");
            sb.AppendLine($"    [DataRow({literals})]");
        }

        var paramList = string.Join(", ", method.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"));
        var argList = string.Join(", ", method.Parameters.Select(p => p.Name));

        sb.AppendLine($"    public void {method.Name}_Theory({paramList})");
        sb.AppendLine("    {");
        if (method.IsStatic)
        {
            sb.AppendLine($"        {targetType.Name}.{method.Name}({argList});");
        }
        else
        {
            sb.AppendLine($"        var sut = {SutCreation(targetType, todoNotes)};");
            sb.AppendLine($"        sut.{method.Name}({argList});");
        }
        sb.AppendLine("        // TODO: assert");
        sb.AppendLine("    }");
    }

    private static bool ReturnsTask(IMethodSymbol method)
    {
        var rt = method.ReturnType;
        if (rt is null) return false;
        var name = rt.Name;
        if (string.Equals(name, "Task", StringComparison.Ordinal) ||
            string.Equals(name, "ValueTask", StringComparison.Ordinal))
        {
            return string.Equals(
                rt.ContainingNamespace?.ToDisplayString(),
                "System.Threading.Tasks",
                StringComparison.Ordinal);
        }
        return false;
    }

    private static string SuggestFilePath(
        LoadedSolution loaded,
        INamedTypeSymbol targetType,
        List<string> todoNotes)
    {
        var prodProject = loaded.Solution.Projects
            .FirstOrDefault(p => ContainsType(p, targetType));

        if (prodProject is null)
        {
            todoNotes.Add($"Could not locate production project for {targetType.Name}; using placeholder path.");
            return $"tests/{targetType.Name}Tests.cs";
        }

        var prodProjectName = prodProject.Name;
        var testProject = loaded.Solution.Projects
            .Where(p => p.ProjectReferences.Any(r => r.ProjectId == prodProject.Id))
            .Where(p => TestFrameworkDetector.DetectFramework(p) is not null)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        if (testProject is null)
        {
            todoNotes.Add($"No test project references {prodProjectName}; using placeholder path.");
            return $"tests/{prodProjectName}.Tests/{targetType.Name}Tests.cs";
        }

        return $"tests/{testProject.Name}/{targetType.Name}Tests.cs";
    }

    private static bool ContainsType(Project project, INamedTypeSymbol type)
    {
        foreach (var loc in type.Locations)
        {
            if (!loc.IsInSource || loc.SourceTree is null) continue;
            if (project.Documents.Any(d => d.FilePath == loc.SourceTree.FilePath))
                return true;
        }
        return false;
    }
}
