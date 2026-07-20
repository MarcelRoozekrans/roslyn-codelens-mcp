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

    // 2
    [Fact]
    public void Forbid_Satisfied()
        => Assert.Empty(Run(Layered(), [Forbid("Demo.Domain.*", "Demo.Infrastructure.*")]));

    // 3
    [Fact]
    public void AllowOnly_CatchesUnlistedDependency()
    {
        var violation = Assert.Single(Run(Layered(), [AllowOnly("Demo.Infrastructure.*", "Demo.Shared.*")]));

        Assert.Equal("allowOnly", violation.RuleKind);
        Assert.Equal("Demo.Infrastructure", violation.SourceScope);
        Assert.Equal("Demo.Domain", violation.TargetScope);
    }

    // 4
    [Fact]
    public void AllowOnly_SatisfiedWhenListed()
        => Assert.Empty(Run(Layered(), [AllowOnly("Demo.Infrastructure.*", "Demo.Domain.*")]));

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

    [Fact]
    public void UnknownScope_IsInvalidArgument()
    {
        var ex = Assert.Throws<McpToolException>(() => Run(Layered(),
            [Forbid("Demo.Infrastructure.*", "Demo.Domain.*")], scope: "assembly"));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
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
