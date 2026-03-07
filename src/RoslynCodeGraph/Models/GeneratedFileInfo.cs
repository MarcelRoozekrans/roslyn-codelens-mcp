namespace RoslynCodeGraph.Models;

public record GeneratedFileInfo(
    string FilePath,
    string Project,
    string? GeneratorName,
    List<string> DefinedTypes,
    string SourceText);
