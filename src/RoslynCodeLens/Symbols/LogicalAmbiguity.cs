namespace RoslynCodeLens.Symbols;

/// <summary>
/// The candidates of an ambiguous match (one per logical group) and the hint on how to resolve it.
/// </summary>
internal sealed record LogicalAmbiguity(IReadOnlyList<string> Candidates, string Hint);
