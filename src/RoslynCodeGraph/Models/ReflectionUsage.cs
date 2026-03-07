namespace RoslynCodeGraph.Models;

public record ReflectionUsage(
    string Kind,     // "dynamic_instantiation", "method_invoke", "assembly_scan", "attribute_discovery"
    string Target,
    string File,
    int Line,
    string Snippet,
    bool IsGenerated = false);
