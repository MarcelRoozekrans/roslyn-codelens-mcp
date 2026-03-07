namespace RoslynCodeGraph.Models;

public record SourceGeneratorInfo(
    string GeneratorName,
    string Project,
    int GeneratedFileCount,
    List<string> GeneratedFiles);
