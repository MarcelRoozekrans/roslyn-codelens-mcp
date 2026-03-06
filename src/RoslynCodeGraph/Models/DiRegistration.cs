namespace RoslynCodeGraph.Models;

public record DiRegistration(
    string Service,
    string Implementation,
    string Lifetime,
    string File,
    int Line);
