using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class GetDiRegistrationsLogic
{
    public static IReadOnlyList<DiRegistration> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        string symbol,
        CancellationToken cancellationToken = default)
        => DiRegistrationScanner.Scan(loaded, resolver, symbol, cancellationToken);
}
