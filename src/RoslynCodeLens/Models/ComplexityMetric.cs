namespace RoslynCodeLens.Models;

/// <param name="MethodName">
/// The member's name. A constructor is named after its type, matching how the other tools render one.
/// </param>
/// <param name="Complexity">
/// Cyclomatic complexity. Keeps its original name so existing consumers stay shape-compatible.
/// Starts at 1 for a branch-free member.
/// </param>
/// <param name="Cognitive">
/// Cognitive complexity (SonarSource). Starts at <b>0</b>, not 1 — a 0 here is not a bug.
/// </param>
/// <param name="MaxNesting">Deepest control-structure nesting inside the member.</param>
public record ComplexityMetric(
    string MethodName,
    string TypeName,
    int Complexity,
    int Cognitive,
    int MaxNesting,
    string File,
    int Line,
    string Project);
