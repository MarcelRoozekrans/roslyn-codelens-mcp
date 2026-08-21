namespace RoslynCodeLens.Models;

public record SymbolReference(
    string ReferenceKind,
    string File,
    int Line,
    int Column,
    string Snippet,
    string Project,
    bool IsGenerated = false,
    /// <summary>
    /// When <see cref="File"/> is generator output produced from markup, the .razor/.cshtml the
    /// user actually wrote. Null otherwise, and omitted from the response so ordinary C# results
    /// are unchanged. No line is carried: component markup compiles to scaffolding that has no
    /// #line mapping back to the authored file.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? GeneratedFrom = null);
