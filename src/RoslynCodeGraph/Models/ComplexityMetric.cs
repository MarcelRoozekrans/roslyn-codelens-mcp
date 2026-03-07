namespace RoslynCodeGraph.Models;

public record ComplexityMetric(string MethodName, string TypeName, int Complexity, string File, int Line, string Project);
