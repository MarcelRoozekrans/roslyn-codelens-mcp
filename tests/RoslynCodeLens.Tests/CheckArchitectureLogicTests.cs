using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

public class CheckArchitectureLogicTests
{
    private static ArchitectureRule Forbid(string from, string to, string? description = null)
        => new("forbid", from, [to], description);

    private static ArchitectureRule AllowOnly(string from, params string[] to)
        => new("allowOnly", from, to);

    /// <summary>
    /// Two projects so cross-project edges and `scope: "project"` are real. Repo deliberately
    /// writes `Demo.Domain.Order` fully qualified with NO using directive — the case a
    /// usings-based scan cannot see.
    /// </summary>
    private static (LoadedSolution Loaded, SymbolResolver Resolver) Layered() => RenameTestWorkspace.Create(
        ("Domain", new[] { ("Order.cs", """
            namespace Demo.Domain;
            public class Order { public int Id; }
            """) }),
        ("Infra", new[] { ("Repo.cs", """
            namespace Demo.Infrastructure;
            public class Repo { public Demo.Domain.Order? Load() => null; }
            """) }));

    private static IReadOnlyList<ArchitectureViolation> Run(
        (LoadedSolution Loaded, SymbolResolver Resolver) workspace,
        IReadOnlyList<ArchitectureRule> rules,
        string scope = "namespace",
        int maxSitesPerViolation = 5)
        => CheckArchitectureLogic.Execute(
            workspace.Loaded, workspace.Resolver, rules, scope, maxSitesPerViolation);

    // 1
    [Fact]
    public void Forbid_Violated()
    {
        var result = Run(Layered(), [Forbid("Demo.Infrastructure.*", "Demo.Domain.*", "layering")]);

        var violation = Assert.Single(result);
        Assert.Equal("forbid", violation.RuleKind);
        Assert.Equal("layering", violation.RuleDescription);
        Assert.Equal("Demo.Infrastructure", violation.SourceScope);
        Assert.Equal("Demo.Domain", violation.TargetScope);
        Assert.True(violation.ReferenceCount >= 1);

        var site = Assert.Single(violation.Sites);
        Assert.EndsWith("Repo.cs", site.File, StringComparison.Ordinal);
        Assert.True(site.Line > 0);
        Assert.True(site.Column > 0);
        Assert.Contains("Repo", site.SourceSymbol, StringComparison.Ordinal);
        Assert.Contains("Order", site.TargetSymbol, StringComparison.Ordinal);
    }

    // 2 — Domain does not reference Infrastructure. The second assertion is the control: the
    // same fixture DOES produce a violation the other way round, so this test cannot pass just
    // because Execute returned nothing.
    [Fact]
    public void Forbid_Satisfied()
    {
        var workspace = Layered();

        Assert.Empty(Run(workspace, [Forbid("Demo.Domain.*", "Demo.Infrastructure.*")]));
        Assert.Single(Run(workspace, [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")]));
    }

    // 3
    [Fact]
    public void AllowOnly_CatchesUnlistedDependency()
    {
        var violation = Assert.Single(Run(Layered(), [AllowOnly("Demo.Infrastructure.*", "Demo.Shared.*")]));

        Assert.Equal("allowOnly", violation.RuleKind);
        Assert.Equal("Demo.Infrastructure", violation.SourceScope);
        Assert.Equal("Demo.Domain", violation.TargetScope);
    }

    // 4 — with the control: drop Demo.Domain from the allow-list and the same fixture fires.
    [Fact]
    public void AllowOnly_SatisfiedWhenListed()
    {
        var workspace = Layered();

        Assert.Empty(Run(workspace, [AllowOnly("Demo.Infrastructure.*", "Demo.Domain.*")]));
        Assert.Single(Run(workspace, [AllowOnly("Demo.Infrastructure.*", "Demo.Shared.*")]));
    }

    // 5 — THE key semantic: allowOnly considers only solution-internal targets.
    [Fact]
    public void AllowOnly_IgnoresFrameworkTargets()
    {
        var workspace = RenameTestWorkspace.Create(
            ("Bag.cs", """
             namespace Demo.Domain;
             public class Bag { public System.Collections.Generic.List<int>? Items; }
             """));

        Assert.Empty(Run(workspace, [AllowOnly("Demo.Domain.*", "Demo.Shared.*")]));

        // Control: the reference IS there and IS reachable — a `forbid` naming it fires on the
        // very same fixture, so the emptiness above is the metadata rule, not a dead walk.
        Assert.Single(Run(workspace, [Forbid("Demo.Domain.*", "System.Collections.Generic")]));
    }

    // 6 — the contrast: forbid DOES evaluate metadata targets.
    [Fact]
    public void Forbid_CanTargetFrameworkNamespace()
    {
        var workspace = RenameTestWorkspace.Create(
            ("Bag.cs", """
             namespace Demo.Domain;
             public class Bag { public System.Collections.Generic.List<int>? Items; }
             """));

        var violation = Assert.Single(
            Run(workspace, [Forbid("Demo.Domain.*", "System.Collections.Generic")]));

        Assert.Equal("Demo.Domain", violation.SourceScope);
        Assert.Equal("System.Collections.Generic", violation.TargetScope);
    }

    // 7 — self-references are never a violation, under either kind.
    [Fact]
    public void SelfReference_IsAllowed()
    {
        var workspace = RenameTestWorkspace.Create(
            ("Order.cs", """
             namespace Demo.Domain;
             public class Order { public int Id; }
             public class OrderService { public Order? Current; }
             """));

        Assert.Empty(Run(workspace, [Forbid("Demo.Domain.*", "Demo.Domain.*")]));
        Assert.Empty(Run(workspace, [AllowOnly("Demo.Domain.*", "Demo.Nothing.AtAll")]));

        // Control: the OrderService -> Order reference is real and visible. Put the two types in
        // different namespaces and the identical rules fire — what was suppressed above is the
        // self-reference, not the walk.
        var split = RenameTestWorkspace.Create(
            ("Order.cs", """
             namespace Demo.Domain;
             public class Order { public int Id; }
             """),
            ("Service.cs", """
             namespace Demo.Services;
             public class OrderService { public Demo.Domain.Order? Current; }
             """));

        Assert.Single(Run(split, [Forbid("Demo.Services.*", "Demo.Domain.*")]));
        Assert.Single(Run(split, [AllowOnly("Demo.Services.*", "Demo.Nothing.AtAll")]));
    }

    // 8 — the test that justifies a semantic tool over the usings-based one. Do not weaken.
    [Fact]
    public void FullyQualifiedReference_WithNoUsing_IsDetected()
    {
        var workspace = Layered();

        // Guard the premise: the fixture really has no using directive.
        var repo = workspace.Loaded.Solution.Projects
            .SelectMany(p => p.Documents)
            .Single(d => string.Equals(d.Name, "Repo.cs", StringComparison.Ordinal));
        Assert.DoesNotContain("using", repo.GetTextAsync().GetAwaiter().GetResult().ToString(),
            StringComparison.Ordinal);

        var violation = Assert.Single(Run(workspace, [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")]));
        Assert.Equal("Demo.Domain", violation.TargetScope);
    }

    // 8b — a `using` directive is NOT itself a dependency. The tool's description promises
    // "an unused `using` is not reported"; a file-level using also sits in the GLOBAL namespace,
    // so counting it accuses the wrong scope entirely.
    [Fact]
    public void FileLevelUsingStatic_WithNoOtherUsage_IsNotReported()
    {
        var workspace = RenameTestWorkspace.Create(
            ("Domain", new[] { ("Constants.cs", """
                namespace Demo.Domain;
                public static class Constants { public const int Zero = 0; }
                """) }),
            ("Infra", new[] { ("Repo.cs", """
                using static Demo.Domain.Constants;
                namespace Demo.Infrastructure;
                public class Repo { public int Id; }
                """) }));

        // `*` so the global namespace the using lives in is in scope: without the fix this
        // reports a violation whose SourceScope is "" — an accusation against nobody.
        Assert.Empty(Run(workspace, [Forbid("*", "Demo.Domain.*")]));

        // Control: add one real use of what the using imports and exactly one violation appears,
        // attributed to the DECLARING namespace rather than the global one.
        var used = RenameTestWorkspace.Create(
            ("Domain", new[] { ("Constants.cs", """
                namespace Demo.Domain;
                public static class Constants { public const int Zero = 0; }
                """) }),
            ("Infra", new[] { ("Repo.cs", """
                using static Demo.Domain.Constants;
                namespace Demo.Infrastructure;
                public class Repo { public int Id = Zero; }
                """) }));

        var violation = Assert.Single(Run(used, [Forbid("*", "Demo.Domain.*")]));
        Assert.Equal("Demo.Infrastructure", violation.SourceScope);
        Assert.Equal(1, violation.ReferenceCount);
    }

    // 8c — an in-namespace using alias must not double-count the one real usage.
    [Fact]
    public void UsingAlias_CountsOnlyTheActualUsage()
    {
        var workspace = RenameTestWorkspace.Create(
            ("Domain", new[] { ("Order.cs", """
                namespace Demo.Domain;
                public class Order { public int Id; }
                """) }),
            ("Infra", new[] { ("Repo.cs", """
                namespace Demo.Infrastructure;
                using Alias = Demo.Domain.Order;
                public class Repo { public Alias? A; }
                """) }));

        var violation = Assert.Single(Run(workspace, [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")]));

        Assert.Equal("Demo.Infrastructure", violation.SourceScope);
        Assert.Equal(1, violation.ReferenceCount);
    }

    // 8d — `var` is a SimpleNameSyntax whose symbol is the INFERRED type, so counting it turns
    // one written reference into two. A human reading this method counts two dependencies on
    // Order: the `new Order()` and the `o.Id` member access.
    [Fact]
    public void Var_IsNotCountedAsAReference()
    {
        var workspace = RenameTestWorkspace.Create(
            ("Domain", new[] { ("Order.cs", """
                namespace Demo.Domain;
                public class Order { public int Id; }
                """) }),
            ("Infra", new[] { ("Repo.cs", """
                namespace Demo.Infrastructure;
                public class Repo
                {
                    public int Load()
                    {
                        var o = new Demo.Domain.Order();
                        return o.Id;
                    }
                }
                """) }));

        var violation = Assert.Single(Run(workspace, [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")]));

        Assert.Equal(2, violation.ReferenceCount);
        Assert.Equal(2, violation.Sites.Count);
    }

    // 8e — an object initializer's member names are not separate dependencies. A human reading
    // `new Demo.Domain.Order { Id = 1, Name = "x" }` points at Order ONCE; counting `Id` and
    // `Name` (whose ContainingType is Order) turns one written reference into three. Same
    // over-count class as `var`.
    [Fact]
    public void ObjectInitializerMembers_AreNotCountedSeparately()
    {
        var violation = Assert.Single(
            Run(InitializerWorkspace("""
                public object Make() => new Demo.Domain.Order { Id = 1, Name = "x" };
                """),
                [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")]));

        Assert.Equal(1, violation.ReferenceCount);
        Assert.Equal(1, violation.Sites.Count);
    }

    // 8f — nested and `with` initializers take the same path: the member being assigned belongs
    // to the type the initializer is initializing, which is already counted.
    [Fact]
    public void NestedInitializerMembers_AreNotCountedSeparately()
    {
        var violation = Assert.Single(
            Run(InitializerWorkspace("""
                public object Make() => new Demo.Domain.Box { Inner = { Id = 1, Name = "x" } };
                """),
                [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")]));

        // One: the single written type name, `Demo.Domain.Box`. `Inner` is a member of the type
        // the outer initializer initializes, and `Id`/`Name` are members of the type the NESTED
        // one initializes, so all three are the same over-count `new Order { Id = 1 }` avoids.
        // `Order` itself is never written here — like `var`, an inferred type is not a reference a
        // reader would point at.
        Assert.Equal(1, violation.ReferenceCount);
    }

    // 8g — the control for 8e: assignment to a member through a LOCAL is not an initializer and
    // is a genuine, separately written reference. It must still count.
    [Fact]
    public void MemberAssignmentThroughALocal_StillCounts()
    {
        var violation = Assert.Single(
            Run(InitializerWorkspace("""
                public void Set(Demo.Domain.Order other) { other.Id = 1; }
                """),
                [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")]));

        // The parameter type, and the `other.Id` member access.
        Assert.Equal(2, violation.ReferenceCount);
    }

    private static (LoadedSolution Loaded, SymbolResolver Resolver) InitializerWorkspace(string body)
        => RenameTestWorkspace.Create(
            ("Domain", new[] { ("Order.cs", """
                namespace Demo.Domain;
                public class Order { public int Id { get; set; } public string? Name { get; set; } }
                public class Box { public Order Inner { get; } = new(); }
                """) }),
            ("Infra", new[] { ("Repo.cs", $$"""
                namespace Demo.Infrastructure;
                public class Repo
                {
                    {{body}}
                }
                """) }));

    // A site inside a field declaration must still name the field. GetDeclaredSymbol on a
    // FieldDeclarationSyntax returns null (a declaration can declare several variables), so a
    // naive walk reports an empty SourceSymbol — a violation site with no owner.
    [Fact]
    public void FieldDeclarationSite_NamesTheField()
    {
        var workspace = RenameTestWorkspace.Create(
            ("Domain", new[] { ("Order.cs", """
                namespace Demo.Domain;
                public class Order { public int Id; }
                """) }),
            ("Infra", new[] { ("Repo.cs", """
                namespace Demo.Infrastructure;
                public class Repo { public Demo.Domain.Order? A; }
                """) }));

        var site = Assert.Single(
            Assert.Single(Run(workspace, [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")])).Sites);

        Assert.Equal("Demo.Infrastructure.Repo.A", site.SourceSymbol, StringComparer.Ordinal);
    }

    // 9 — grouping: 5 human-visible references, sites capped at 2.
    [Fact]
    public void Grouping_CountsAllReferencesButLimitsSites()
    {
        var workspace = RenameTestWorkspace.Create(
            ("Domain", new[] { ("Order.cs", """
                namespace Demo.Domain;
                public class Order { public static readonly int Zero = 0; }
                """) }),
            ("Infra", new[] { ("Repo.cs", """
                namespace Demo.Infrastructure;
                public class Repo
                {
                    public Demo.Domain.Order? A;
                    public Demo.Domain.Order? B;
                    public Demo.Domain.Order? C;
                    public int D() => Demo.Domain.Order.Zero;
                    public int E() => Demo.Domain.Order.Zero;
                }
                """) }));

        var violation = Assert.Single(
            Run(workspace, [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")], maxSitesPerViolation: 2));

        Assert.Equal(5, violation.ReferenceCount);
        Assert.Equal(2, violation.Sites.Count);
    }

    // 10
    [Fact]
    public void ProjectScope_UsesProjectNames()
    {
        var violation = Assert.Single(Run(Layered(), [Forbid("Infra", "Domain")], scope: "project"));

        Assert.Equal("Infra", violation.SourceScope);
        Assert.Equal("Domain", violation.TargetScope);
    }

    // 10b — a LINKED file (one path, compiled into two projects) has a DIFFERENT source scope in
    // each project under `scope: "project"`. Deduping the walk by path alone attributes it to
    // whichever project was enumerated first and silently drops the other project's violations —
    // exactly the case a linked-file repo would rely on the tool to catch.
    [Fact]
    public void LinkedFile_UnderProjectScope_IsReportedForEveryProject()
    {
        var violations = Run(LinkedFileWorkspace(), [Forbid("App.*", "Domain")], scope: "project");

        Assert.Equal(2, violations.Count);
        Assert.Equal(
            new[] { "App.A", "App.B" },
            violations.Select(v => v.SourceScope).OrderBy(s => s, StringComparer.Ordinal));
        Assert.All(violations, v => Assert.Equal("Domain", v.TargetScope));
    }

    // 10c — the counterpart: under `scope: "namespace"` the source scope of a linked file is the
    // SAME in every compilation, so walking it once per project would double-count a single
    // written reference. The dedupe must still apply there.
    [Fact]
    public void LinkedFile_UnderNamespaceScope_IsStillCountedOnce()
    {
        var violation = Assert.Single(
            Run(LinkedFileWorkspace(), [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")]));

        Assert.Equal(1, violation.ReferenceCount);
        Assert.Equal(1, violation.Sites.Count);
    }

    /// <summary>
    /// One source path compiled into two projects — the shape a `&lt;Compile Include="..\Shared.cs" /&gt;`
    /// link produces. Both documents carry the identical FilePath, which is what makes them linked.
    /// </summary>
    private static (LoadedSolution Loaded, SymbolResolver Resolver) LinkedFileWorkspace()
    {
        const string Linked = """
            namespace Demo.Infrastructure;
            public class Repo { public Demo.Domain.Order? A; }
            """;

        return RenameTestWorkspace.Create(
            ("Domain", new[] { ("Order.cs", """
                namespace Demo.Domain;
                public class Order { public int Id; }
                """) }),
            ("App.A", new[] { ("Shared.cs", Linked) }),
            ("App.B", new[] { ("Shared.cs", Linked) }));
    }

    // 11
    [Fact]
    public void GeneratedCode_IsSkipped()
    {
        var workspace = RenameTestWorkspace.Create(
            ("Domain", new[] { ("Order.cs", """
                namespace Demo.Domain;
                public class Order { public int Id; }
                """) }),
            ("Infra", new[] { ("Repo.g.cs", """
                namespace Demo.Infrastructure;
                public class Repo { public Demo.Domain.Order? Load() => null; }
                """) }));

        Assert.Empty(Run(workspace, [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")]));

        // Control: the identical file under a non-generated name DOES violate, so the emptiness
        // above is the generated-source skip and nothing else.
        var handWritten = RenameTestWorkspace.Create(
            ("Domain", new[] { ("Order.cs", """
                namespace Demo.Domain;
                public class Order { public int Id; }
                """) }),
            ("Infra", new[] { ("Repo.cs", """
                namespace Demo.Infrastructure;
                public class Repo { public Demo.Domain.Order? Load() => null; }
                """) }));

        Assert.Single(Run(handWritten, [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")]));
    }

    // 11b — the target side. allowOnly's target set is open-ended, so generator output (regex
    // implementations, JSON contexts, DI registries) would show up as "unlisted dependencies"
    // the user can neither list naturally nor delete. Mirrors the metadata-target rule.
    [Fact]
    public void AllowOnly_IgnoresGeneratedTargets()
    {
        var workspace = GeneratedTargetWorkspace();

        Assert.Empty(Run(workspace, [AllowOnly("Demo.Infrastructure.*", "Demo.Shared.*")]));

        // Control: an identical allowOnly over the same fixture with the target hand-written
        // DOES fire, so the emptiness above is the generated-target rule, not a dead walk.
        var handWritten = RenameTestWorkspace.Create(
            ("Domain", new[] { ("Order.cs", """
                namespace Demo.Domain;
                public class Order { public int Id; }
                """) }),
            ("Infra", new[] { ("Repo.cs", """
                namespace Demo.Infrastructure;
                public class Repo { public Demo.Domain.Order? A; }
                """) }));

        Assert.Single(Run(handWritten, [AllowOnly("Demo.Infrastructure.*", "Demo.Shared.*")]));
    }

    // 11c — the contrast, exactly as with metadata targets: an explicit `forbid` names its
    // target, so it DOES evaluate generated ones.
    [Fact]
    public void Forbid_StillTargetsGeneratedCode()
    {
        var violation = Assert.Single(
            Run(GeneratedTargetWorkspace(), [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")]));

        Assert.Equal("Demo.Domain", violation.TargetScope);
    }

    private static (LoadedSolution Loaded, SymbolResolver Resolver) GeneratedTargetWorkspace()
        => RenameTestWorkspace.Create(
            ("Domain", new[] { ("Order.g.cs", """
                namespace Demo.Domain;
                public class Order { public int Id; }
                """) }),
            ("Infra", new[] { ("Repo.cs", """
                namespace Demo.Infrastructure;
                public class Repo { public Demo.Domain.Order? A; }
                """) }));

    // 11d — "solution-internal" must not depend on HOW the solution loaded. When a
    // ProjectReference resolves as a metadata reference instead (a real MSBuildWorkspace failure
    // mode in this repo), Locations.IsInSource goes false and EVERY allowOnly rule over that
    // boundary silently returns empty — indistinguishable from clean architecture.
    [Fact]
    public void AllowOnly_SeesTargetsWhoseProjectResolvedAsMetadata()
    {
        var workspace = MetadataResolvedReference();

        var violation = Assert.Single(
            Run(workspace, [AllowOnly("Demo.Infrastructure.*", "Demo.Shared.*")]));

        Assert.Equal("Demo.Domain", violation.TargetScope);
    }

    [Fact]
    public void IsSolutionInternal_TrueForMetadataTypeFromASolutionProject()
    {
        var workspace = MetadataResolvedReference();
        var order = OrderAsSeenByInfra(workspace);

        // Guard the premise: this really is a metadata symbol, not a source one.
        Assert.DoesNotContain(order.Locations, l => l.IsInSource);
        Assert.Equal("Domain", order.ContainingAssembly.Name, StringComparer.Ordinal);

        Assert.True(CheckArchitectureLogic.IsSolutionInternal(
            order, CheckArchitectureLogic.SolutionScopeNames(workspace.Loaded.Solution)));
    }

    [Fact]
    public void IsSolutionInternal_FalseForAnAssemblyNoProjectProduces()
    {
        var workspace = MetadataResolvedReference();
        var order = OrderAsSeenByInfra(workspace);

        Assert.False(CheckArchitectureLogic.IsSolutionInternal(
            order, new HashSet<string>(StringComparer.Ordinal) { "SomethingElse" }));
    }

    private static INamedTypeSymbol OrderAsSeenByInfra(
        (LoadedSolution Loaded, SymbolResolver Resolver) workspace)
    {
        var infra = workspace.Loaded.Solution.Projects
            .Single(p => string.Equals(p.Name, "Infra", StringComparison.Ordinal));
        return workspace.Loaded.Compilations[infra.Id].GetTypeByMetadataName("Demo.Domain.Order")!;
    }

    /// <summary>
    /// Two projects where Infra sees Domain's types ONLY through a compiled image, with no
    /// ProjectReference — the shape a dropped project reference produces.
    /// </summary>
    private static (LoadedSolution Loaded, SymbolResolver Resolver) MetadataResolvedReference()
    {
        const string DomainSource = """
            namespace Demo.Domain;
            public class Order { public int Id; }
            """;

        var corlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

        var domainImage = CSharpCompilation.Create(
            "Domain", [CSharpSyntaxTree.ParseText(DomainSource)], [corlib], options);
        using var stream = new MemoryStream();
        Assert.True(domainImage.Emit(stream).Success);
        var domainMetadata = MetadataReference.CreateFromImage(stream.ToArray());

        var solution = new AdhocWorkspace().CurrentSolution;

        var domainId = ProjectId.CreateNewId();
        solution = solution
            .AddProject(ProjectInfo.Create(domainId, VersionStamp.Create(), "Domain", "Domain",
                    LanguageNames.CSharp, compilationOptions: options)
                .WithMetadataReferences([corlib]))
            .AddDocument(DocumentId.CreateNewId(domainId), "Order.cs", DomainSource,
                filePath: "Order.cs");

        var infraId = ProjectId.CreateNewId();
        solution = solution
            .AddProject(ProjectInfo.Create(infraId, VersionStamp.Create(), "Infra", "Infra",
                    LanguageNames.CSharp, compilationOptions: options)
                .WithMetadataReferences([corlib, domainMetadata]))
            .AddDocument(DocumentId.CreateNewId(infraId), "Repo.cs", """
                namespace Demo.Infrastructure;
                public class Repo { public Demo.Domain.Order A; }
                """, filePath: "Repo.cs");

        var compilations = new ConcurrentDictionary<ProjectId, Compilation>();
        foreach (var project in solution.Projects)
            compilations[project.Id] = project.GetCompilationAsync().GetAwaiter().GetResult()!;

        var loaded = new LoadedSolution { Solution = solution, Compilations = compilations };
        return (loaded, new SymbolResolver(loaded));
    }

    // 12
    [Fact]
    public void ExactPatternDoesNotMatchChildren()
    {
        var workspace = RenameTestWorkspace.Create(
            ("Domain", new[] { ("Thing.cs", """
                namespace Demo.Domain.Orders;
                public class Thing { public int Id; }
                """) }),
            ("Infra", new[] { ("Use.cs", """
                namespace Demo.Infrastructure;
                public class Use { public Demo.Domain.Orders.Thing? T; }
                """) }));

        Assert.Empty(Run(workspace, [Forbid("Demo.Infrastructure.*", "Demo.Domain")]));
        Assert.Single(Run(workspace, [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")]));
    }

    // 12b — the FROM side, end to end. Nothing else pins that an exact `from` is exact: every
    // other from-pattern in this file is a wildcard, so a prefix-matching bug would go unseen.
    // "Demo.Infra" must not match the source scope "Demo.Infrastructure".
    [Fact]
    public void ExactFromPattern_DoesNotPrefixMatchALongerSourceScope()
    {
        var workspace = Layered();

        Assert.Empty(Run(workspace, [Forbid("Demo.Infra", "Demo.Domain.*")]));

        var violation = Assert.Single(Run(workspace, [Forbid("Demo.Infrastructure", "Demo.Domain.*")]));
        Assert.Equal("Demo.Infrastructure", violation.SourceScope);
    }

    // 12c — and the from side must not match a CHILD of an exact pattern either, while the
    // wildcard form does.
    [Fact]
    public void ExactFromPattern_DoesNotMatchChildScopes()
    {
        var workspace = RenameTestWorkspace.Create(
            ("Domain", new[] { ("Order.cs", """
                namespace Demo.Domain;
                public class Order { public int Id; }
                """) }),
            ("Infra", new[] { ("Repo.cs", """
                namespace Demo.Infrastructure.Persistence;
                public class Repo { public Demo.Domain.Order? A; }
                """) }));

        Assert.Empty(Run(workspace, [Forbid("Demo.Infrastructure", "Demo.Domain.*")]));

        var violation = Assert.Single(Run(workspace, [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")]));
        Assert.Equal("Demo.Infrastructure.Persistence", violation.SourceScope);
    }

    // 13 — error cases
    [Fact]
    public void EmptyRules_IsInvalidArgument()
    {
        var ex = Assert.Throws<McpToolException>(() => Run(Layered(), []));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public void UnknownKind_IsInvalidArgumentNamingRuleIndex()
    {
        var ex = Assert.Throws<McpToolException>(() => Run(Layered(),
            [Forbid("Demo.Domain.*", "Demo.Infrastructure.*"), new ArchitectureRule("banish", "A", ["B"])]));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowOnlyWithEmptyTo_IsInvalidArgument()
    {
        var ex = Assert.Throws<McpToolException>(() => Run(Layered(),
            [new ArchitectureRule("allowOnly", "Demo.Api.*", [])]));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedPattern_IsInvalidArgument()
    {
        var ex = Assert.Throws<McpToolException>(() => Run(Layered(),
            [Forbid("Demo.*.Domain", "Demo.Infrastructure.*")]));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedTargetPattern_IsInvalidArgument()
    {
        var ex = Assert.Throws<McpToolException>(() => Run(Layered(),
            [Forbid("Demo.Infrastructure.*", "*.Domain")]));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    // A violation with no sites is unactionable: the caller gets an accusation and no location.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveMaxSitesPerViolation_IsInvalidArgument(int maxSites)
    {
        var ex = Assert.Throws<McpToolException>(() => Run(Layered(),
            [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")], maxSitesPerViolation: maxSites));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("maxSitesPerViolation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownScope_IsInvalidArgument()
    {
        var ex = Assert.Throws<McpToolException>(() => Run(Layered(),
            [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")], scope: "assembly"));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    // Summary buckets are per RULE AS WRITTEN. Keying on the matched `to` pattern split one
    // forbid with several `to` patterns across several buckets while `rulesEvaluated` said 1 —
    // the summary contradicted itself.
    [Fact]
    public void Summary_OneWrittenRuleIsOneBucket()
    {
        var workspace = RenameTestWorkspace.Create(
            ("Domain", new[] { ("Types.cs", """
                namespace Demo.Domain;
                public class Order { public int Id; }
                """), ("More.cs", """
                namespace Demo.Shared;
                public class Helper { public int Id; }
                """) }),
            ("Infra", new[] { ("Repo.cs", """
                namespace Demo.Infrastructure;
                public class Repo
                {
                    public Demo.Domain.Order? A;
                    public Demo.Shared.Helper? B;
                }
                """) }));

        var rules = new[]
        {
            new ArchitectureRule("forbid", "Demo.Infrastructure.*", ["Demo.Domain.*", "Demo.Shared.*"]),
        };

        var raw = Run(workspace, rules);
        Assert.Equal(2, raw.Count);                     // two edges...
        Assert.All(raw, v => Assert.Equal(0, v.RuleIndex));

        var byRule = ByRule(CheckArchitectureTool.BuildSummary(raw, rules));

        var bucket = Assert.Single(byRule);             // ...but one rule, so one bucket
        Assert.Equal(2, bucket.Value);
    }

    [Fact]
    public void Summary_KeepsTwoRulesSharingAFromPatternApart()
    {
        var workspace = RenameTestWorkspace.Create(
            ("Domain", new[] { ("Order.cs", """
                namespace Demo.Domain;
                public class Order { public int Id; }
                """) }),
            ("Infra", new[] { ("Repo.cs", """
                namespace Demo.Infrastructure;
                public class Repo { public Demo.Domain.Order? A; }
                """) }));

        var rules = new[]
        {
            new ArchitectureRule("forbid", "Demo.Infrastructure.*", ["Demo.Domain.*"]),
            new ArchitectureRule("allowOnly", "Demo.Infrastructure.*", ["Demo.Shared.*"]),
        };

        Assert.Equal(2, ByRule(CheckArchitectureTool.BuildSummary(Run(workspace, rules), rules)).Count);
    }

    /// <summary>Reads the summary's `byRule` map off the wire shape it is actually serialized as.</summary>
    private static IReadOnlyDictionary<string, int> ByRule(object summary)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(summary));
        return document.RootElement.GetProperty("byRule").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.Ordinal);
    }

    // 14
    [Fact]
    public void Sorting_WorstFirstWithinRuleOrder()
    {
        var workspace = RenameTestWorkspace.Create(
            ("Domain", new[] { ("Types.cs", """
                namespace Demo.Domain;
                public class Order { public int Id; }
                """), ("More.cs", """
                namespace Demo.Shared;
                public class Helper { public int Id; }
                """) }),
            ("Infra", new[] { ("Repo.cs", """
                namespace Demo.Infrastructure;
                public class Repo
                {
                    public Demo.Domain.Order? A;
                    public Demo.Domain.Order? B;
                    public Demo.Shared.Helper? C;
                }
                """) }));

        var result = Run(workspace, [
            AllowOnly("Demo.Infrastructure.*", "Demo.Nothing"),   // rule 0 → two groups
            Forbid("Demo.Infrastructure.*", "Demo.Shared.*"),     // rule 1 → one group
        ]);

        Assert.Equal(3, result.Count);
        // Rule 0 first, worst group first within it.
        Assert.Equal("allowOnly", result[0].RuleKind);
        Assert.Equal("Demo.Domain", result[0].TargetScope);
        Assert.Equal(2, result[0].ReferenceCount);
        Assert.Equal("allowOnly", result[1].RuleKind);
        Assert.Equal("Demo.Shared", result[1].TargetScope);
        Assert.Equal(1, result[1].ReferenceCount);
        Assert.Equal("forbid", result[2].RuleKind);
    }

    // Source-side filtering must not skip a tree that declares several namespaces,
    // only one of which a rule cares about.
    [Fact]
    public void TreeWithMultipleNamespaces_IsNotSkipped()
    {
        var workspace = RenameTestWorkspace.Create(
            ("Domain", new[] { ("Order.cs", """
                namespace Demo.Domain;
                public class Order { public int Id; }
                """) }),
            ("Infra", new[] { ("Mixed.cs", """
                namespace Demo.Unrelated
                {
                    public class Bystander { public int Id; }
                }
                namespace Demo.Infrastructure
                {
                    public class Repo { public Demo.Domain.Order? Load() => null; }
                }
                """) }));

        var violation = Assert.Single(Run(workspace, [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")]));
        Assert.Equal("Demo.Infrastructure", violation.SourceScope);
    }

    // The other half of source-side filtering: a tree declaring only namespaces no rule names is
    // dropped BEFORE a semantic model is built, which is what keeps cost proportional to the rules
    // rather than to solution size. Observable here as "its references are never evaluated" — the
    // Bystander below writes a genuine cross-namespace reference that the rule's From excludes.
    [Fact]
    public void TreeDeclaringOnlyUnmatchedNamespaces_IsSkipped()
    {
        var workspace = RenameTestWorkspace.Create(
            ("Domain", new[] { ("Order.cs", """
                namespace Demo.Domain;
                public class Order { public int Id; }
                """) }),
            ("Infra", new[] { ("Bystander.cs", """
                namespace Demo.Unrelated;
                public class Bystander { public Demo.Domain.Order? Load() => null; }
                """) }));

        Assert.Empty(Run(workspace, [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")]));
    }

    // ...and the same filter asserted where it actually lives: in the COUNT. The test above passes
    // whether or not the filter exists — an unmatched tree yields no violation either way, it just
    // costs a semantic model to find that out. Removing the filter therefore breaks nothing
    // visible while turning this tool from O(rules) into O(solution). Four trees here, one of them
    // declaring a namespace any rule's From can match: exactly one model may be built.
    [Fact]
    public void OnlyTreesMatchingARuleSource_GetASemanticModel()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("Order.cs", """
                namespace Demo.Domain;
                public class Order { public int Id; }
                """),
            ("Repo.cs", """
                namespace Demo.Infrastructure;
                public class Repo { public Demo.Domain.Order? Load() => null; }
                """),
            ("Report.cs", """
                namespace Demo.Reporting;
                public class Report { public Demo.Domain.Order? Source; }
                """),
            ("Util.cs", """
                namespace Demo.Util;
                public class Util { public Demo.Domain.Order? Cached; }
                """));

        var created = 0;
        var violations = CheckArchitectureLogic.ExecuteCore(
            loaded, resolver,
            [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")],
            "namespace", 5,
            modelFactory: (compilation, tree) =>
            {
                created++;
                return compilation.GetSemanticModel(tree);
            });

        // The control: the one tree that IS bound still produces its violation, so a count of 1
        // cannot come from the scan having quietly done nothing.
        Assert.Equal("Demo.Infrastructure", Assert.Single(violations).SourceScope);
        Assert.Equal(1, created);
    }

    // The global namespace is a real source scope, reachable via `*`.
    [Fact]
    public void GlobalNamespaceSource_IsAnalysedUnderMatchAll()
    {
        var workspace = RenameTestWorkspace.Create(
            ("Domain", new[] { ("Order.cs", """
                namespace Demo.Domain;
                public class Order { public int Id; }
                """) }),
            ("Infra", new[] { ("Loose.cs", """
                public class Loose { public Demo.Domain.Order? Load() => null; }
                """) }));

        var violation = Assert.Single(Run(workspace, [Forbid("*", "Demo.Domain.*")]));
        Assert.Equal("", violation.SourceScope);
        Assert.Equal("Demo.Domain", violation.TargetScope);
    }
}
