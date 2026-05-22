using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindEventSubscribersToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;

    public FindEventSubscribersToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

    [Fact]
    public void UnknownSymbol_ReturnsEmpty()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "Does.Not.Exist");

        Assert.Empty(results);
    }

    [Fact]
    public void Subscribe_MethodGroup_ReportsHandlerFqn()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");

        var match = Assert.Single(results, r =>
            r.Kind == SubscriptionKind.Subscribe &&
            r.HandlerName.Contains("OnClicked", StringComparison.Ordinal) &&
            r.FilePath.EndsWith("EventSubscriberSamples.cs", StringComparison.Ordinal) &&
            !r.Snippet.Contains("Clicked2", StringComparison.Ordinal));

        Assert.True(match.Line > 0);
        Assert.Equal("TestLib", match.Project);
    }

    [Fact]
    public void Subscribe_Lambda_ReportsSyntheticName()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");

        Assert.Contains(results, r =>
            r.Kind == SubscriptionKind.Subscribe &&
            r.HandlerName.StartsWith("<lambda at ", StringComparison.Ordinal) &&
            r.HandlerName.Contains("EventSubscriberSamples.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Subscribe_AnonymousMethod_ReportsSyntheticName()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");

        Assert.Contains(results, r =>
            r.Kind == SubscriptionKind.Subscribe &&
            r.HandlerName.StartsWith("<anonymous-method at ", StringComparison.Ordinal));
    }

    [Fact]
    public void Unsubscribe_TaggedAsUnsubscribe()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");

        Assert.Contains(results, r =>
            r.Kind == SubscriptionKind.Unsubscribe &&
            r.HandlerName.Contains("OnClicked", StringComparison.Ordinal));
    }

    [Fact]
    public void InterfaceEvent_MatchesImplementations()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "IBusEventPublisher.MessageReceived");

        Assert.True(results.Count >= 2,
            $"Expected >=2 subscribers across implementations, got {results.Count}");
    }

    [Fact]
    public void TwoSubscriptionsOnSameLine_BothReported()
    {
        var clickedResults = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");
        var clicked2Results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked2");

        Assert.Contains(clickedResults, r =>
            r.Snippet.Contains("Clicked", StringComparison.Ordinal) &&
            !r.Snippet.Contains("Clicked2", StringComparison.Ordinal));
        Assert.Contains(clicked2Results, r =>
            r.Snippet.Contains("Clicked2", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_SortedByFileLine()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");

        for (int i = 1; i < results.Count; i++)
        {
            var prev = results[i - 1];
            var curr = results[i];
            var fileCmp = string.CompareOrdinal(prev.FilePath, curr.FilePath);
            Assert.True(
                fileCmp < 0 || (fileCmp == 0 && prev.Line <= curr.Line),
                $"Sort violation at {i}: '{prev.FilePath}:{prev.Line}' before '{curr.FilePath}:{curr.Line}'");
        }
    }

    [Fact]
    public void EventName_IsFullyQualified()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");

        Assert.NotEmpty(results);
        foreach (var r in results)
        {
            Assert.Contains("Clicked", r.EventName, StringComparison.Ordinal);
            Assert.NotEmpty(r.Project);
        }
    }

    [Fact]
    public void Sort_OrdersByFilePathThenLine()
    {
        var input = new List<EventSubscriberInfo>
        {
            new("E", "H", SubscriptionKind.Subscribe, "b.cs", 1, "x", "P", false),
            new("E", "H", SubscriptionKind.Subscribe, "a.cs", 9, "x", "P", false),
            new("E", "H", SubscriptionKind.Subscribe, "a.cs", 2, "x", "P", false),
        };

        var sorted = FindEventSubscribersTool.Sort(input);

        Assert.Collection(sorted,
            e => { Assert.Equal("a.cs", e.FilePath); Assert.Equal(2, e.Line); },
            e => { Assert.Equal("a.cs", e.FilePath); Assert.Equal(9, e.Line); },
            e => { Assert.Equal("b.cs", e.FilePath); Assert.Equal(1, e.Line); });
    }
}
