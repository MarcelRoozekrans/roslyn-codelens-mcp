using RoslynCodeLens;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

/// <summary>
/// The obsolete-target set comes from <see cref="SymbolResolver"/>, which dedupes types by display
/// name across compilations and therefore keeps whichever compilation indexed one FIRST — a
/// ConcurrentDictionary enumeration, so an arbitrary choice that differs run to run. The usage side
/// binds under the compilation <c>SolutionScanner</c> deterministically picked for the tree. When
/// those two disagree, symbol-identity matching finds nothing and the usage silently VANISHES.
/// </summary>
public class FindObsoleteUsageCrossCompilationTests
{
    private const int Iterations = 20;

    private const string ApiSource = """
        namespace Demo;
        public class Api
        {
            [System.Obsolete("Use NewWay instead")]
            public void Legacy() { }
        }
        """;

    private const string UserSource = """
        namespace Demo;
        public class User
        {
            public void Call() { new Api().Legacy(); }
        }
        """;

    [Fact]
    public void LinkedFiles_InTwoProjects_FindTheUsageOnEveryRun()
    {
        // Independent workspace per iteration: the choice under test is made when the resolver is
        // built, so reusing one workspace would freeze whichever outcome the first build happened
        // to produce and let the defect pass by luck.
        var counts = new List<int>(Iterations);

        for (var i = 0; i < Iterations; i++)
        {
            var (loaded, resolver) = RenameTestWorkspace.Create(
                ("ProjA", [(@"C:\sln\Api.cs", ApiSource), (@"C:\sln\User.cs", UserSource)]),
                ("ProjB", [(@"C:\sln\Api.cs", ApiSource), (@"C:\sln\User.cs", UserSource)]));

            var result = FindObsoleteUsageLogic.Execute(loaded, resolver, project: null, errorOnly: false);
            counts.Add(result.Groups.Sum(g => g.UsageCount));
        }

        Assert.Equal(
            string.Join(",", Enumerable.Repeat(1, Iterations)),
            string.Join(",", counts));
    }
}
