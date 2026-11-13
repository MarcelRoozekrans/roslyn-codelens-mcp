namespace RoslynCodeLens.Analysis;

/// <summary>
/// The identity of a source generator as Roslyn uses it to build the synthetic path of the files
/// it emits: <c>&lt;AssemblyName&gt;/&lt;TypeName&gt;/&lt;hintName&gt;</c>.
/// </summary>
internal readonly record struct GeneratorIdentity(string AssemblyName, string TypeName);
