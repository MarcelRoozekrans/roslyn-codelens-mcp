namespace RoslynCodeLens.Models;

/// <summary>
/// One edit to a parameter list, applied in order against the original parameters.
/// Kind: remove | reorder | add.
/// remove  → Parameter names the parameter to drop.
/// reorder → Order is a full permutation of the names surviving at that point.
/// add     → Name/Type plus CallSiteValue (required: what every existing call site passes).
///           DefaultValue makes the parameter optional, letting existing calls omit it instead.
/// </summary>
public record SignatureOperation(
    string Kind,
    string? Parameter = null,
    IReadOnlyList<string>? Order = null,
    string? Name = null,
    string? Type = null,
    string? CallSiteValue = null,
    string? DefaultValue = null);
