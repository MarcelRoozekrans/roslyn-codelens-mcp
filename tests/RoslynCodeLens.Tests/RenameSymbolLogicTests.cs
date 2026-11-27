using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

public class RenameSymbolLogicTests
{
    private const string BasicSource = """
        namespace RenameDemo;

        public class Widget
        {
            public Widget() { }
            public int Compute(int value) => value + 1;
            public int Compute(int a, int b) => a + b;
            // Widget appears in this comment.
            public string Marker = "Widget in a string";
            public string Describe() => nameof(Widget);
        }

        public class Gadget
        {
            public int Run() => new Widget().Compute(1);
        }
        """;

    private static Task<RenameSymbolResult> RunAsync(
        LoadedSolution loaded, SymbolResolver resolver, string symbol, string newName,
        bool renameOverloads = true, bool renameInStrings = false, bool renameInComments = true,
        bool preview = true, bool force = false,
        CommitWrittenDocuments? commit = null)
        => RenameSymbolLogic.ExecuteAsync(
            loaded, resolver, symbol, newName,
            renameOverloads, renameInStrings, renameInComments, preview, force,
            commit, CancellationToken.None);

    /// <summary>Rewraps a workspace as a degraded load (non-empty LoadDiagnostics).</summary>
    private static (LoadedSolution Loaded, SymbolResolver Resolver) Degrade(
        (LoadedSolution Loaded, SymbolResolver Resolver) workspace)
    {
        var degraded = new LoadedSolution
        {
            Solution = workspace.Loaded.Solution,
            Compilations = workspace.Loaded.Compilations,
            LoadDiagnostics = ["RenameProj: metadata reference 'Missing.dll' could not be resolved"],
        };
        return (degraded, new SymbolResolver(degraded));
    }

    /// <summary>
    /// Adds a tree to every compilation of a workspace without adding it to the solution's documents, the way a source
    /// generator's output appears.
    /// </summary>
    private static (LoadedSolution Loaded, SymbolResolver Resolver) WithGeneratedTree(
        (LoadedSolution Loaded, SymbolResolver Resolver) workspace, string filePath, string source)
    {
        var generated = new LoadedSolution
        {
            Solution = workspace.Loaded.Solution,
            Compilations = new ConcurrentDictionary<ProjectId, Compilation>(workspace.Loaded.Compilations.Select(
                pair => KeyValuePair.Create(pair.Key, pair.Value.AddSyntaxTrees(CSharpSyntaxTree.ParseText(source, path: filePath))))),
        };
        return (generated, new SymbolResolver(generated));
    }

    /// <summary>
    /// Lib declares <paramref name="libSource"/> against version 1 of an assembly Dep; App references Lib and version 2 of
    /// Dep, so App sees Lib through a retargeted assembly symbol. Gamma, unrelated, declares <paramref name="gammaSource"/>.
    /// </summary>
    private static (LoadedSolution Loaded, SymbolResolver Resolver) WithRetargetedProject(string libSource, string gammaSource)
    {
        var corlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        var solution = new AdhocWorkspace().CurrentSolution;
        var ids = new List<ProjectId>();
        (string Name, MetadataReference[] References, string Path, string Source)[] projects =
        [
            ("Lib", [corlib, Dep("1.0.0.0")], "Lib/Helper.cs", libSource),
            ("App", [corlib, Dep("2.0.0.0")], "App/App.cs", "namespace App; public class Consumer { }"),
            ("Gamma", [corlib], "Gamma/Helper.cs", gammaSource),
        ];
        foreach (var (name, references, path, source) in projects)
        {
            var id = ProjectId.CreateNewId();
            solution = solution
                .AddProject(ProjectInfo.Create(id, VersionStamp.Create(), name, name, LanguageNames.CSharp, compilationOptions: options)
                    .WithMetadataReferences(references)
                    .WithProjectReferences(name == "App" ? [new ProjectReference(ids[0])] : []))
                .AddDocument(DocumentId.CreateNewId(id), System.IO.Path.GetFileName(path), source, filePath: path);
            ids.Add(id);
        }

        var loaded = new LoadedSolution
        {
            Solution = solution,
            Compilations = new ConcurrentDictionary<ProjectId, Compilation>(
                ids.Select(id => KeyValuePair.Create(id, solution.GetProject(id)!.GetCompilationAsync().GetAwaiter().GetResult()!))),
        };
        return (loaded, new SymbolResolver(loaded));

        MetadataReference Dep(string version)
        {
            var dep = CSharpCompilation.Create("Dep",
                [CSharpSyntaxTree.ParseText($"[assembly: System.Reflection.AssemblyVersion(\"{version}\")] namespace Dep {{ public class D {{ }} }}")],
                [corlib], options);
            using var stream = new MemoryStream();
            var emitted = dep.Emit(stream);
            Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));
            return MetadataReference.CreateFromImage(stream.ToArray());
        }
    }

    [Fact]
    public async Task InvalidIdentifier_ThrowsInvalidArgument()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Widget", "123 bad name"));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task UnknownSymbol_ThrowsSymbolNotFound()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "NoSuchType", "Whatever"));
        Assert.Equal(ToolErrorCode.SymbolNotFound, ex.Code);
    }

    [Fact]
    public async Task AmbiguousSimpleName_ThrowsAmbiguousMatch()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("A.cs", "namespace NsA; public class Dup { }"),
            ("B.cs", "namespace NsB; public class Dup { }"));
        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Dup", "Renamed"));
        Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
    }

    [Theory]
    [InlineData("Duplicate.Helper", "Duplicate.Helper")]
    [InlineData("Duplicate.Helper.Common", "Duplicate.Helper.Common()")]
    public async Task FullNameDeclaredInTwoProjects_ThrowsAmbiguousMatchNamingTheProjects(string symbol, string candidate)
    {
        // Two distinct types that share a full name: no name selects one of them, so renaming only the first would
        // silently leave the other project behind. Beta references Alpha (the builder chains projects), which only
        // adds a CS0436 warning in Beta and does not merge the two types.
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("Alpha", [("Alpha/Helper.cs", "namespace Duplicate; public class Helper { public int Common() => 1; }")]),
            ("Beta", [("Beta/Helper.cs", "namespace Duplicate; public class Helper { public int Common() => 2; }")]));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, symbol, "Renamed"));

        Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
        Assert.Equal(
            $"Symbol '{symbol}' matched 2 distinct symbols: {candidate} (project Alpha), {candidate} (project Beta). "
                + "Symbols that share a full name in different projects or assemblies cannot be selected by name.",
            ex.Message);
    }

    [Fact]
    public void ResolveSingleTarget_FullNameDeclaredInTwoProjectsWithTheSameGeneratedPart_ThrowsAmbiguousMatchNamingTheProjects()
    {
        // A generator runs in each project. Without an output directory its documents get the same relative path, and
        // generating the same part gives the same span. That is no linked file, so the two types stay distinct. The
        // generated tree is not in the solution, so the test stops at target resolution instead of running Renamer.
        var (_, resolver) = WithGeneratedTree(
            RenameTestWorkspace.Create(
                ("Alpha", [("Alpha/Helper.cs", "namespace Duplicate; public partial class Helper { }")]),
                ("Beta", [("Beta/Helper.cs", "namespace Duplicate; public partial class Helper { }")])),
            "Gen/Gen.Generator/Helper.g.cs",
            "namespace Duplicate; partial class Helper { }");

        var ex = Assert.Throws<McpToolException>(() => RenameSymbolLogic.ResolveSingleTarget(resolver, "Duplicate.Helper"));

        Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
        Assert.Equal(
            "Symbol 'Duplicate.Helper' matched 2 distinct symbols: Duplicate.Helper (project Alpha), Duplicate.Helper (project Beta). "
                + "Symbols that share a full name in different projects or assemblies cannot be selected by name.",
            ex.Message);
    }

    [Fact]
    public void ResolveSingleTarget_GeneratedMemberInTwoProjects_NamesEachProject()
    {
        // Both generated documents have the same path, so the path cannot tell which project a member comes from; the
        // compilation that declares it can.
        var (_, resolver) = WithGeneratedTree(
            RenameTestWorkspace.Create(
                ("Alpha", [("Alpha/Helper.cs", "namespace Duplicate; public partial class Helper { }")]),
                ("Beta", [("Beta/Helper.cs", "namespace Duplicate; public partial class Helper { }")])),
            "Gen/Gen.Generator/Helper.g.cs",
            "namespace Duplicate; partial class Helper { public int Generated() => 1; }");

        var ex = Assert.Throws<McpToolException>(() => RenameSymbolLogic.ResolveSingleTarget(resolver, "Duplicate.Helper.Generated"));

        Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
        Assert.Equal(
            "Symbol 'Duplicate.Helper.Generated' matched 2 distinct symbols: Duplicate.Helper.Generated() (project Alpha), "
                + "Duplicate.Helper.Generated() (project Beta). Symbols that share a full name in different projects or "
                + "assemblies cannot be selected by name.",
            ex.Message);
    }

    [Fact]
    public void ResolveSingleTarget_FullNameDeclaredInALinkedFileAndAnotherProject_NamesEveryProjectOfTheLinkedFile()
    {
        // The linked declaration is one target compiled into Alpha and Beta; naming only one of them would hide that
        // renaming it changes both projects.
        var (_, resolver) = RenameTestWorkspace.Create(
            ("Alpha", [("Shared/Helper.cs", "namespace Duplicate; public class Helper { public int Common() => 1; }")]),
            ("Beta", [("Shared/Helper.cs", "namespace Duplicate; public class Helper { public int Common() => 1; }")]),
            ("Gamma", [("Gamma/Helper.cs", "namespace Duplicate; public class Helper { public int Common() => 3; }")]));

        var ex = Assert.Throws<McpToolException>(() => RenameSymbolLogic.ResolveSingleTarget(resolver, "Duplicate.Helper.Common"));

        Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
        Assert.Equal(
            "Symbol 'Duplicate.Helper.Common' matched 2 distinct symbols: Duplicate.Helper.Common() (projects Alpha, Beta), "
                + "Duplicate.Helper.Common() (project Gamma). Symbols that share a full name in different projects or "
                + "assemblies cannot be selected by name.",
            ex.Message);
    }

    [Fact]
    public void ResolveSingleTarget_FullNameOfARetargetedProjectAndAnotherProject_NamesBothProjects()
    {
        // App comes first, so the index holds Lib's type as App sees it: through a retargeted assembly symbol, which is
        // not Lib's own compilation assembly but still declared by Lib.
        var (_, resolver) = WithRetargetedProject(
            "namespace Duplicate; public class Helper { public Dep.D Use() => null; public int Common() => 1; }",
            "namespace Duplicate; public class Helper { public int Common() => 3; }");

        var ex = Assert.Throws<McpToolException>(() => RenameSymbolLogic.ResolveSingleTarget(resolver, "Duplicate.Helper.Common"));

        Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
        Assert.Equal(
            "Symbol 'Duplicate.Helper.Common' matched 2 distinct symbols: Duplicate.Helper.Common() (project Lib), "
                + "Duplicate.Helper.Common() (project Gamma). Symbols that share a full name in different projects or "
                + "assemblies cannot be selected by name.",
            ex.Message);
    }

    // A partial member whose definition each project declares itself, with the implementation in a linked file: the
    // definitions do not point at the implementation, yet the linked part makes the projects' members one target.
    [Theory]
    [InlineData(
        "Demo.Helper.Compute",
        "public partial int Compute(int value);",
        "public partial int Compute(int value) => value; public int Use() => Compute(1);",
        "Compute")]
    [InlineData(
        "Demo.Helper.Value",
        "public partial int Value { get; }",
        "public partial int Value => 1; public int Use() => Value;",
        "Value")]
    public async Task PartialMemberWithDefinitionPerProjectAndLinkedImplementation_RenamesEveryPart(
        string symbol, string definition, string implementation, string name)
    {
        var alpha = $"namespace Demo; public partial class Helper {{ {definition} }}";
        var beta = $"namespace Demo; public partial class Helper {{ public int Beta() => 2; {definition} }}";
        var shared = $"namespace Demo; public partial class Helper {{ {implementation} }}";
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("Alpha", [("Alpha/Helper.cs", alpha), ("Shared/Helper.Impl.cs", shared)]),
            ("Beta", [("Beta/Helper.cs", beta), ("Shared/Helper.Impl.cs", shared)]));

        var result = await RunAsync(loaded, resolver, symbol, "Renamed");

        Assert.True(result.Success, result.Message);
        Assert.Equal(3, result.FilesChanged);
        // Each linked document reports the same change, so the edits are compared without duplicates.
        Assert.Equal(
            [
                ("Alpha/Helper.cs", alpha.Replace(name, "Renamed", StringComparison.Ordinal)),
                ("Beta/Helper.cs", beta.Replace(name, "Renamed", StringComparison.Ordinal)),
                ("Shared/Helper.Impl.cs", shared.Replace(name, "Renamed", StringComparison.Ordinal)),
            ],
            result.Edits.Select(e => (e.FilePath, e.NewText)).Distinct().Order());
    }

    [Fact]
    public async Task AmbiguousNameWithSharedFullName_HintsBothWaysOut()
    {
        // Other.Helper.Common can still be selected by a more qualified name; only the two Duplicate.Helper.Common
        // methods cannot, so the message gives both hints.
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("Alpha", [
                ("Alpha/Helper.cs", "namespace Duplicate; public class Helper { public int Common() => 1; }"),
                ("Alpha/Other.cs", "namespace Other; public class Helper { public int Common() => 3; }"),
            ]),
            ("Beta", [("Beta/Helper.cs", "namespace Duplicate; public class Helper { public int Common() => 2; }")]));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Helper.Common", "Renamed"));

        Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
        Assert.Equal(
            "Symbol 'Helper.Common' matched 3 distinct symbols: Duplicate.Helper.Common() (project Alpha), Other.Helper.Common(), "
                + "Duplicate.Helper.Common() (project Beta). Use a more qualified name; symbols that share a full name in "
                + "different projects or assemblies cannot be selected by name.",
            ex.Message);
    }

    [Fact]
    public async Task AmbiguousNameWithLinkedFileInTwoNamespaces_ListsTheLinkedDeclarationByItsUniqueName()
    {
        // The linked file compiles into Third in Alpha and into First in Beta. Only Third.Helper.Common selects it alone;
        // First.Helper.Common matches Gamma's method as well, so Gamma's candidate cannot be selected by name. That holds
        // although First is not the name the linked declaration has in its first project.
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("Alpha", [("Shared/Helper.cs", "namespace Third; public class Helper { public int Common() => 1; }")]),
            ("Beta", [("Shared/Helper.cs", "namespace First; public class Helper { public int Common() => 1; }")]),
            ("Gamma", [("Gamma/Helper.cs", "namespace First; public class Helper { public int Common() => 3; }")]));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Helper.Common", "Renamed"));

        Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
        Assert.Equal(
            "Symbol 'Helper.Common' matched 2 distinct symbols: Third.Helper.Common(), First.Helper.Common() (project Gamma). "
                + "Use a more qualified name; symbols that share a full name in different projects or assemblies cannot be "
                + "selected by name.",
            ex.Message);
    }

    [Fact]
    public void ConstructorTarget_ThrowsInvalidArgument()
    {
        var (loaded, _) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var compilation = loaded.Compilations.Values.First();
        var widget = compilation.GetTypeByMetadataName("RenameDemo.Widget")!;
        var ctor = widget.InstanceConstructors.First(c => !c.IsImplicitlyDeclared);

        var ex = Assert.Throws<McpToolException>(
            () => RenameSymbolLogic.ValidateRenameTarget(ctor, "Widget.Widget"));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public void MetadataTarget_ThrowsInvalidArgument()
    {
        var (loaded, _) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var compilation = loaded.Compilations.Values.First();
        var stringType = compilation.GetSpecialType(SpecialType.System_String);

        var ex = Assert.Throws<McpToolException>(
            () => RenameSymbolLogic.ValidateRenameTarget(stringType, "System.String"));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task MethodOverloadGroup_IsNotAmbiguous()
    {
        // Widget.Compute has two overloads; that is ONE rename target, not an ambiguity.
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var result = await RunAsync(loaded, resolver, "Widget.Compute", "Calculate");
        Assert.True(result.Success);
    }

    [Fact]
    public async Task QualifiedNameWithoutTypeParameters_RenamesGenericType()
    {
        // Finding 6: "Data.Repository" must resolve Repository<T>, matching the tool's
        // documented "fully qualified (Namespace.MyClass)" contract.
        const string source = """
            namespace Data;

            public class Repository<T>
            {
                public T? GetById(int id) => default;
            }
            """;
        var (loaded, resolver) = RenameTestWorkspace.Create(("Repository.cs", source));

        var result = await RunAsync(loaded, resolver, "Data.Repository", "Store");

        Assert.True(result.Success);
        Assert.False(result.Applied);
        Assert.Equal("Data.Repository<T>", result.OldName);
        var renamed = ApplyEditsToSource(source, result.Edits, "Repository.cs");
        Assert.Contains("class Store<T>", renamed, StringComparison.Ordinal);
        Assert.DoesNotContain("Repository", renamed, StringComparison.Ordinal);
    }

    private static string ApplyEditsToSource(string source, IEnumerable<TextEdit> edits, string filePath)
    {
        var text = Microsoft.CodeAnalysis.Text.SourceText.From(source);
        var changes = edits
            .Where(e => string.Equals(e.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            .Select(e => new Microsoft.CodeAnalysis.Text.TextChange(
                Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(
                    text.Lines[e.StartLine - 1].Start + e.StartColumn - 1,
                    text.Lines[e.EndLine - 1].Start + e.EndColumn - 1),
                e.NewText));
        return text.WithChanges(changes).ToString();
    }

    [Fact]
    public async Task RenameType_CascadesToUsagesCtorAndNameof()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var result = await RunAsync(loaded, resolver, "Widget", "Sprocket");

        Assert.True(result.Success);
        Assert.False(result.Applied);
        Assert.Empty(result.Conflicts);

        var after = ApplyEditsToSource(BasicSource, result.Edits, "Widget.cs");
        Assert.Contains("public class Sprocket", after, StringComparison.Ordinal);
        Assert.Contains("public Sprocket()", after, StringComparison.Ordinal);
        Assert.Contains("new Sprocket().Compute(1)", after, StringComparison.Ordinal);
        Assert.Contains("nameof(Sprocket)", after, StringComparison.Ordinal);
        Assert.DoesNotContain("class Widget", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenameInComments_OnByDefault_RewritesComment()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var result = await RunAsync(loaded, resolver, "Widget", "Sprocket");
        var after = ApplyEditsToSource(BasicSource, result.Edits, "Widget.cs");
        Assert.Contains("// Sprocket appears in this comment.", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenameInComments_Off_LeavesComment()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var result = await RunAsync(loaded, resolver, "Widget", "Sprocket", renameInComments: false);
        var after = ApplyEditsToSource(BasicSource, result.Edits, "Widget.cs");
        Assert.Contains("// Widget appears in this comment.", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenameInStrings_OffByDefault_LeavesString()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var result = await RunAsync(loaded, resolver, "Widget", "Sprocket");
        var after = ApplyEditsToSource(BasicSource, result.Edits, "Widget.cs");
        Assert.Contains("\"Widget in a string\"", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenameInStrings_On_RewritesString()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var result = await RunAsync(loaded, resolver, "Widget", "Sprocket", renameInStrings: true);
        var after = ApplyEditsToSource(BasicSource, result.Edits, "Widget.cs");
        Assert.Contains("\"Sprocket in a string\"", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenameOverloads_On_RenamesAllOverloadsAndCallSites()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var result = await RunAsync(loaded, resolver, "Widget.Compute", "Calculate");
        var after = ApplyEditsToSource(BasicSource, result.Edits, "Widget.cs");
        Assert.Contains("public int Calculate(int value)", after, StringComparison.Ordinal);
        Assert.Contains("public int Calculate(int a, int b)", after, StringComparison.Ordinal);
        Assert.Contains(".Calculate(1)", after, StringComparison.Ordinal);
        Assert.Empty(result.Conflicts);
    }

    private const string CollisionSource = """
        namespace RenameDemo;
        public class First { }
        public class Second { }
        """;

    [Fact]
    public async Task CollidingRename_Preview_ReportsConflicts()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Types.cs", CollisionSource));
        var result = await RunAsync(loaded, resolver, "First", "Second");

        Assert.True(result.Success);
        Assert.False(result.Applied);
        Assert.NotEmpty(result.Conflicts);   // CS0101: duplicate type in namespace
        Assert.Contains(result.Conflicts, c => string.Equals(c.Id, "CS0101", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CollidingRename_Apply_RefusesWithoutForce()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Types.cs", CollisionSource));
        var result = await RunAsync(loaded, resolver, "First", "Second", preview: false);

        Assert.False(result.Success);
        Assert.False(result.Applied);
        Assert.NotEmpty(result.Conflicts);
        Assert.Contains("force", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_WritesRenamedFilesToDisk()
    {
        var dir = Directory.CreateTempSubdirectory("rename-apply-").FullName;
        try
        {
            var path = Path.Combine(dir, "Widget.cs");
            await File.WriteAllTextAsync(path, BasicSource);
            var (loaded, resolver) = RenameTestWorkspace.Create((path, BasicSource));

            var result = await RunAsync(loaded, resolver, "Widget", "Sprocket", preview: false);

            Assert.True(result.Success);
            Assert.True(result.Applied);
            var onDisk = await File.ReadAllTextAsync(path);
            Assert.Contains("public class Sprocket", onDisk, StringComparison.Ordinal);
            Assert.DoesNotContain("class Widget", onDisk, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Preview_LeavesDiskUntouched()
    {
        var dir = Directory.CreateTempSubdirectory("rename-preview-").FullName;
        try
        {
            var path = Path.Combine(dir, "Widget.cs");
            await File.WriteAllTextAsync(path, BasicSource);
            var (loaded, resolver) = RenameTestWorkspace.Create((path, BasicSource));

            var result = await RunAsync(loaded, resolver, "Widget", "Sprocket");   // preview: true

            Assert.False(result.Applied);
            Assert.Equal(BasicSource, await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PreexistingErrorMentioningSymbol_IsNotAConflict()
    {
        // CS0117's message embeds the type name ("'Widget' does not contain a
        // definition for 'NoSuchMember'"). After the rename the same error is
        // still there, just mentioning 'Sprocket' — the error COUNT for
        // (CS0117, file) is unchanged, so it must not be reported as a conflict.
        const string source = """
            namespace RenameDemo;

            public class Widget
            {
                public static int Existing = 1;
            }

            public class User
            {
                public object Bad = Widget.NoSuchMember;
                public int Good = Widget.Existing;
            }
            """;
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", source));
        var result = await RunAsync(loaded, resolver, "Widget", "Sprocket");

        Assert.True(result.Success);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public async Task SameIdSameFileNewLine_IsReported()
    {
        // Pre-existing CS0101 on the duplicate 'Dup' (line 3). Renaming
        // First -> Dup introduces a SECOND CS0101 in the same file with the
        // exact same message text — only the line differs. The count-based
        // diff must report exactly the new one, on the new line.
        const string source = """
            namespace RenameDemo;
            public class Dup { }
            public class Dup { }
            public class First { }
            """;
        var (loaded, resolver) = RenameTestWorkspace.Create(("Types.cs", source));
        var result = await RunAsync(loaded, resolver, "First", "Dup");

        Assert.True(result.Success);
        Assert.NotEmpty(result.Conflicts);
        var conflict = Assert.Single(
            result.Conflicts, c => string.Equals(c.Id, "CS0101", StringComparison.Ordinal));
        Assert.Equal(4, conflict.Line);   // the renamed declaration, not the pre-existing dup (line 3)
    }

    // NOTE on the dependent-project-break scenario: a rename that genuinely breaks
    // a downstream project WITHOUT changing its text is not constructible with
    // AdhocWorkspace. Name-capture attempts (rename LibA's Foo -> Action while LibB
    // uses System.Action) are defused by Renamer's own conflict engine, which
    // complexifies the captured reference in LibB to 'System.Action' — so LibB's
    // text changes and it lands in GetProjectChanges anyway (verified empirically).
    // The remaining real-world breaks (source generators, name-based reflection
    // lookups) need generator/analyzer infrastructure AdhocWorkspace doesn't run.
    // The scan-set mechanic is therefore tested directly instead: dependents ARE
    // scanned (ComputeScanSet_IncludesDirectDependents) and scanning them applies
    // the same count-based diff without phantom conflicts or crashes
    // (DependentProjectWithPreexistingError_IsNotAConflict).
    [Fact]
    public async Task ComputeScanSet_IncludesDirectDependents()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("LibA", [("Widget.cs", "namespace Lib; public class Widget { }")]),
            ("LibB", [("Other.cs", "namespace Dep; public class Other { }")]));

        var target = RenameSymbolLogic.ResolveSingleTarget(resolver, "Widget");
        var renamed = await Microsoft.CodeAnalysis.Rename.Renamer.RenameSymbolAsync(
            loaded.Solution, target,
            new Microsoft.CodeAnalysis.Rename.SymbolRenameOptions(), "Sprocket",
            CancellationToken.None);

        var scanSet = SolutionChangeSafety.ComputeScanSet(loaded.Solution, renamed);
        var libA = loaded.Solution.Projects.Single(p => string.Equals(p.Name, "LibA", StringComparison.Ordinal)).Id;
        var libB = loaded.Solution.Projects.Single(p => string.Equals(p.Name, "LibB", StringComparison.Ordinal)).Id;

        Assert.Contains(libA, scanSet);   // textually changed by the rename
        Assert.Contains(libB, scanSet);   // untouched, but a direct dependent of LibA
        Assert.Equal(scanSet.Count, scanSet.Distinct().Count());
    }

    /// <summary>
    /// A break propagates as far as the reference graph does, so the scan set must too: with
    /// LibA &lt;- LibB &lt;- LibC, LibC sees LibA's types through LibB and can break without LibB's
    /// text changing. Scanning only DIRECT dependents left LibC unexamined and the conflict
    /// unreported.
    /// </summary>
    [Fact]
    public async Task ComputeScanSet_IncludesTransitiveDependents()
    {
        var (loaded, resolver) = RenameTestWorkspace.CreateChain(
            ("LibA", [("Widget.cs", "namespace Lib; public class Widget { }")]),
            ("LibB", [("Mid.cs", "namespace Mid; public class Mid { }")]),
            ("LibC", [("Far.cs", "namespace Far; public class Far { }")]));

        var target = RenameSymbolLogic.ResolveSingleTarget(resolver, "Widget");
        var renamed = await Microsoft.CodeAnalysis.Rename.Renamer.RenameSymbolAsync(
            loaded.Solution, target,
            new Microsoft.CodeAnalysis.Rename.SymbolRenameOptions(), "Sprocket",
            CancellationToken.None);

        var byName = loaded.Solution.Projects.ToDictionary(p => p.Name, p => p.Id, StringComparer.Ordinal);

        // Precondition: LibC really is transitive-only, otherwise this proves nothing.
        var direct = loaded.Solution.GetProjectDependencyGraph()
            .GetProjectsThatDirectlyDependOnThisProject(byName["LibA"]);
        Assert.DoesNotContain(byName["LibC"], direct);

        var scanSet = SolutionChangeSafety.ComputeScanSet(loaded.Solution, renamed);

        Assert.Contains(byName["LibA"], scanSet);
        Assert.Contains(byName["LibB"], scanSet);
        Assert.Contains(byName["LibC"], scanSet);
        Assert.Equal(scanSet.Count, scanSet.Distinct().Count());
    }

    [Fact]
    public async Task DependentProjectWithPreexistingError_IsNotAConflict()
    {
        // LibB is scanned as a direct dependent; its pre-existing CS0246 must not
        // surface as a conflict (count unchanged by the rename in LibA).
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("LibA", [("Widget.cs", "namespace Lib; public class Widget { }")]),
            ("LibB", [("Broken.cs", "namespace Dep; public class Broken { public Unknown Field; }")]));
        var result = await RunAsync(loaded, resolver, "Widget", "Sprocket");

        Assert.True(result.Success);
        Assert.Empty(result.Conflicts);
    }

    private static Diagnostic MakeError(string id, string path, int line, bool suppressed = false)
    {
        var linePosition = new Microsoft.CodeAnalysis.Text.LinePositionSpan(
            new Microsoft.CodeAnalysis.Text.LinePosition(line - 1, 0),
            new Microsoft.CodeAnalysis.Text.LinePosition(line - 1, 1));
        var location = Location.Create(
            path, new Microsoft.CodeAnalysis.Text.TextSpan(0, 0), linePosition);
        return Diagnostic.Create(
            id, "Test", "synthetic error",
            DiagnosticSeverity.Error, DiagnosticSeverity.Error,
            isEnabledByDefault: true, warningLevel: 0, isSuppressed: suppressed,
            location: location);
    }

    [Fact]
    public void SuppressedDiagnostics_AreIgnored()
    {
        // An IsSuppressed Error-severity diagnostic is not constructible through
        // AdhocWorkspace compilation options: #pragma/SuppressMessage suppression is
        // applied BEFORE warning-as-error elevation, so a suppressed diagnostic
        // surfaces (under ReportSuppressedDiagnostics) at its original Warning
        // severity, never Error. Suppressors that keep Error severity require
        // CompilationWithAnalyzers. The filter is therefore exercised at the diff
        // level with a synthetic suppressed diagnostic (Diagnostic.Create supports
        // isSuppressed directly).
        var suppressed = MakeError("CS9999", "File.cs", 3, suppressed: true);
        Assert.Empty(SolutionChangeSafety.DiffNewErrors(before: [], after: [suppressed]));

        var unsuppressed = MakeError("CS9999", "File.cs", 3);
        var conflict = Assert.Single(SolutionChangeSafety.DiffNewErrors(before: [], after: [unsuppressed]));
        Assert.Equal("CS9999", conflict.Id);
        Assert.Equal(3, conflict.Line);
    }

    [Fact]
    public void DiffNewErrors_PrefersLinesAbsentFromBefore()
    {
        var before = new[] { MakeError("CS0101", "Types.cs", 3) };
        var after = new[] { MakeError("CS0101", "Types.cs", 3), MakeError("CS0101", "Types.cs", 7) };

        var conflict = Assert.Single(SolutionChangeSafety.DiffNewErrors(before, after));
        Assert.Equal(7, conflict.Line);
    }

    [Fact]
    public async Task Degraded_Preview_MessageCarriesWarning()
    {
        var (loaded, resolver) = Degrade(RenameTestWorkspace.Create(("Widget.cs", BasicSource)));
        var result = await RunAsync(loaded, resolver, "Widget", "Sprocket");

        Assert.True(result.Success);
        Assert.False(result.Applied);
        Assert.Contains("degraded", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 load diagnostic", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("incomplete", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Degraded_Apply_RefusedWithoutForce()
    {
        var commitCalls = 0;
        var (loaded, resolver) = Degrade(RenameTestWorkspace.Create(("Widget.cs", BasicSource)));
        var result = await RunAsync(loaded, resolver, "Widget", "Sprocket", preview: false,
            commit: (_, _) => { commitCalls++; return Task.CompletedTask; });

        Assert.False(result.Success);
        Assert.False(result.Applied);
        Assert.Contains("degraded", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rebuild_solution", result.Message, StringComparison.Ordinal);
        Assert.Contains("force=true", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, commitCalls);
    }

    [Fact]
    public async Task Degraded_Apply_ForceWrites()
    {
        var dir = Directory.CreateTempSubdirectory("rename-degraded-").FullName;
        try
        {
            var path = Path.Combine(dir, "Widget.cs");
            await File.WriteAllTextAsync(path, BasicSource);
            var (loaded, resolver) = Degrade(RenameTestWorkspace.Create((path, BasicSource)));

            var result = await RunAsync(loaded, resolver, "Widget", "Sprocket", preview: false, force: true);

            Assert.True(result.Success);
            Assert.True(result.Applied);
            Assert.Contains("public class Sprocket", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_DiskChangedSinceSnapshot_RefusedAndFileNotOverwritten()
    {
        var dir = Directory.CreateTempSubdirectory("rename-stale-").FullName;
        try
        {
            var path = Path.Combine(dir, "Widget.cs");
            await File.WriteAllTextAsync(path, BasicSource);
            var (loaded, resolver) = RenameTestWorkspace.Create((path, BasicSource));

            // Concurrent edit AFTER the snapshot: the apply must refuse rather than clobber it.
            var drifted = BasicSource + "\n// concurrent edit\n";
            await File.WriteAllTextAsync(path, drifted);

            var commitCalls = 0;
            var result = await RunAsync(loaded, resolver, "Widget", "Sprocket", preview: false,
                commit: (_, _) => { commitCalls++; return Task.CompletedTask; });

            Assert.False(result.Success);
            Assert.False(result.Applied);
            Assert.Contains(path, result.Message, StringComparison.Ordinal);
            Assert.Contains("rebuild_solution", result.Message, StringComparison.Ordinal);
            Assert.Equal(drifted, await File.ReadAllTextAsync(path));
            Assert.Equal(0, commitCalls);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The invariant rename shares with change_signature: force overrides RISK judgements
    /// (conflicts, a degraded load), never the freshness check. The text that would be written was
    /// computed from a snapshot that no longer describes the file, so caller intent cannot make
    /// writing it safe.
    /// </summary>
    [Fact]
    public async Task Apply_DiskChangedSinceSnapshot_ForceStillRefusesAndWritesNothing()
    {
        var dir = Directory.CreateTempSubdirectory("rename-force-stale-").FullName;
        try
        {
            var path = Path.Combine(dir, "Widget.cs");
            await File.WriteAllTextAsync(path, BasicSource);
            var (loaded, resolver) = RenameTestWorkspace.Create((path, BasicSource));

            var drifted = BasicSource + "\n// concurrent edit\n";
            await File.WriteAllTextAsync(path, drifted);

            var commitCalls = 0;
            var result = await RunAsync(loaded, resolver, "Widget", "Sprocket", preview: false, force: true,
                commit: (_, _) => { commitCalls++; return Task.CompletedTask; });

            Assert.False(result.Success);
            Assert.False(result.Applied);
            Assert.Contains("rebuild_solution", result.Message, StringComparison.Ordinal);
            Assert.Equal(drifted, await File.ReadAllTextAsync(path));
            Assert.Equal(0, commitCalls);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_InvokesCommitHookWithExactlyTheChangedDocuments()
    {
        var dir = Directory.CreateTempSubdirectory("rename-commit-").FullName;
        try
        {
            var widgetPath = Path.Combine(dir, "Widget.cs");
            var otherPath = Path.Combine(dir, "Other.cs");
            const string otherSource = "namespace RenameDemo;\npublic class Untouched { }\n";
            await File.WriteAllTextAsync(widgetPath, BasicSource);
            await File.WriteAllTextAsync(otherPath, otherSource);
            var (loaded, resolver) = RenameTestWorkspace.Create(
                (widgetPath, BasicSource), (otherPath, otherSource));

            var commits = new List<IReadOnlyList<(Microsoft.CodeAnalysis.DocumentId Id, Microsoft.CodeAnalysis.Text.SourceText Text)>>();
            var result = await RunAsync(loaded, resolver, "Widget", "Sprocket", preview: false,
                commit: (docs, _) => { commits.Add(docs); return Task.CompletedTask; });

            Assert.True(result.Applied);
            var docs = Assert.Single(commits);
            var pair = Assert.Single(docs);   // only Widget.cs changed; Other.cs must not be committed
            Assert.Equal(loaded.Solution.GetDocumentIdsWithFilePath(widgetPath).Single(), pair.Id);
            Assert.Contains("public class Sprocket", pair.Text.ToString(), StringComparison.Ordinal);
            // The committed text is exactly what landed on disk.
            Assert.Equal(await File.ReadAllTextAsync(widgetPath), pair.Text.ToString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Preview_DoesNotInvokeCommitHook()
    {
        var commitCalls = 0;
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var result = await RunAsync(loaded, resolver, "Widget", "Sprocket",
            commit: (_, _) => { commitCalls++; return Task.CompletedTask; });

        Assert.False(result.Applied);
        Assert.Equal(0, commitCalls);
    }

    [Fact]
    public async Task Apply_CommitHookFailure_DoesNotFailTheRename()
    {
        var dir = Directory.CreateTempSubdirectory("rename-commitfail-").FullName;
        try
        {
            var path = Path.Combine(dir, "Widget.cs");
            await File.WriteAllTextAsync(path, BasicSource);
            var (loaded, resolver) = RenameTestWorkspace.Create((path, BasicSource));

            var result = await RunAsync(loaded, resolver, "Widget", "Sprocket", preview: false,
                commit: (_, _) => throw new InvalidOperationException("commit boom"));

            Assert.True(result.Success);
            Assert.True(result.Applied);   // the disk write DID succeed
            Assert.Contains("commit boom", result.Message, StringComparison.Ordinal);
            Assert.Contains("public class Sprocket", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CollidingRename_ForceTrue_WritesAnyway()
    {
        var dir = Directory.CreateTempSubdirectory("rename-force-").FullName;
        try
        {
            var path = Path.Combine(dir, "Types.cs");
            await File.WriteAllTextAsync(path, CollisionSource);
            var (loaded, resolver) = RenameTestWorkspace.Create((path, CollisionSource));

            var result = await RunAsync(loaded, resolver, "First", "Second", preview: false, force: true);

            Assert.True(result.Applied);
            Assert.NotEmpty(result.Conflicts);
            Assert.Contains("class Second", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
