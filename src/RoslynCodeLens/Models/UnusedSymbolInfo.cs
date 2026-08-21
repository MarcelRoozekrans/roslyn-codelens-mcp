namespace RoslynCodeLens.Models;

public record UnusedSymbolInfo(
    string SymbolName,
    string SymbolKind,
    string File,
    int Line,
    string Project,
    bool IsGenerated = false,
    /// <summary>
    /// For a symbol declared in generator output, the markup file to delete. "NavMenu is unused"
    /// is not actionable while the only path given is inside obj/.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? GeneratedFrom = null);
