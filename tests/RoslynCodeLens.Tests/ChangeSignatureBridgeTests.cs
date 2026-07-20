using Microsoft.CodeAnalysis;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests;

public class ChangeSignatureBridgeTests
{
    /// <summary>
    /// Every internal Roslyn member the bridge needs must resolve. If a Roslyn upgrade moves or
    /// renames one, this fails loudly here rather than at a user's apply — which is the whole
    /// reason the reflection is confined to one probeable surface.
    /// </summary>
    [Fact]
    public void Probe_ResolvesEveryRequiredMember()
    {
        var missing = ChangeSignatureBridge.Probe();
        Assert.Empty(missing);
    }

    /// <summary>
    /// Probe() returning empty only proves the members it LOOKED FOR resolved, so the probe's
    /// coverage is asserted separately. Three things the original probe never checked are pinned
    /// here: the service's parameterless constructor (used via Activator, so its absence would
    /// only ever surface at a user's apply) and the two awaited return types — a signature change
    /// that returned a different Task<T> would break at the GetTaskResult cast instead.
    /// </summary>
    [Fact]
    public void Probe_CoversTheServiceConstructorAndBothAwaitedReturnTypes()
    {
        var probed = ChangeSignatureBridge.ProbedMembers();

        Assert.Contains(probed, name => name.Contains("CSharpChangeSignatureService..ctor()", StringComparison.Ordinal));
        Assert.Contains(probed, name =>
            name.Contains("SemanticDocument.CreateAsync", StringComparison.Ordinal)
            && name.Contains("Task<SemanticDocument>", StringComparison.Ordinal));
        Assert.Contains(probed, name =>
            name.Contains("ChangeSignatureWithContextAsync", StringComparison.Ordinal)
            && name.Contains("Task<ChangeSignatureResult>", StringComparison.Ordinal));
    }

    [Fact]
    public void ProbedMembers_IsASupersetOfWhateverIsMissing()
    {
        var probed = ChangeSignatureBridge.ProbedMembers();

        Assert.NotEmpty(probed);
        Assert.All(ChangeSignatureBridge.Probe(), missing => Assert.Contains(missing, probed));
    }

    [Fact]
    public async Task Reorder_RewritesDeclarationAndCallSite()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Demo.cs", """
            namespace Demo;
            public class Svc
            {
                public int Add(int a, string b) => a;
                public int Use() => Add(1, "x");
            }
            """));
        var method = (IMethodSymbol)resolver.FindSymbols("Svc.Add").Single();
        var doc = loaded.Solution.GetDocument(
            method.DeclaringSyntaxReferences[0].SyntaxTree)!;

        var reordered = new[] { method.Parameters[1], method.Parameters[0] }
            .Select(p => new DesiredParameter(p, null, null, null, null, null)).ToList();

        var result = await ChangeSignatureBridge.ChangeSignatureAsync(doc, method, reordered, default);

        Assert.True(result.Succeeded, result.FailureMessage);
        var text = (await result.UpdatedSolution!.GetDocument(doc.Id)!.GetTextAsync()).ToString();
        Assert.Contains("Add(string b, int a)", text, StringComparison.Ordinal);
        Assert.Contains("Add(\"x\", 1)", text, StringComparison.Ordinal);   // call site followed
    }
}
