using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

/// <summary>
/// Same defect as <see cref="FindObsoleteUsageCrossCompilationTests"/>: the target event comes from
/// the resolver (whichever compilation indexed the type first — an arbitrary, run-varying choice)
/// while the <c>+=</c> is bound under the compilation the scanner picked for the tree. Symbol
/// identity across two compilations is not equality, so every subscription disappears.
/// </summary>
public class FindEventSubscribersCrossCompilationTests
{
    private const int Iterations = 20;

    private const string PublisherSource = """
        namespace Demo;
        public class Publisher
        {
            public event System.EventHandler? Clicked;
        }
        """;

    private const string SubscriberSource = """
        namespace Demo;
        public class Subscriber
        {
            public void Wire(Publisher p) { p.Clicked += OnClicked; }
            private void OnClicked(object? sender, System.EventArgs e) { }
        }
        """;

    [Fact]
    public void LinkedFiles_InTwoProjects_FindTheSubscriptionOnEveryRun()
    {
        var counts = new List<int>(Iterations);

        for (var i = 0; i < Iterations; i++)
        {
            var (loaded, resolver) = RenameTestWorkspace.Create(
                ("ProjA", [(@"C:\sln\Publisher.cs", PublisherSource), (@"C:\sln\Subscriber.cs", SubscriberSource)]),
                ("ProjB", [(@"C:\sln\Publisher.cs", PublisherSource), (@"C:\sln\Subscriber.cs", SubscriberSource)]));

            var results = FindEventSubscribersLogic.Execute(
                loaded, resolver, new MetadataSymbolResolver(loaded, resolver), "Demo.Publisher.Clicked");
            counts.Add(results.Count);
        }

        Assert.Equal(
            string.Join(",", Enumerable.Repeat(1, Iterations)),
            string.Join(",", counts));
    }
}
