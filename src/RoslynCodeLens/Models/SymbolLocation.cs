namespace RoslynCodeLens.Models;

public record SymbolLocation(
    string Type,       // "class", "struct", "record"
    string FullName,
    string File,
    int Line,
    string Project,
    bool IsGenerated = false,
    SymbolOrigin? Origin = null,
    /// <summary>Markup file this symbol's generated declaration came from, when applicable.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? GeneratedFrom = null);
