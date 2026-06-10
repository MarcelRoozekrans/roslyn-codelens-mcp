using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tests.Symbols;

public class SymbolSignatureComparerTests
{
	// Builds two separate compilations that model the cross-project scenario:
	// - libCompilation: defines IFoo in assembly "Lib"
	// - consumerCompilation: references Lib as metadata, so its view of IFoo
	//   is a metadata symbol — a different object than the source symbol in libCompilation.
	private static (INamedTypeSymbol sourceSymbol, INamedTypeSymbol metadataSymbol) BuildCrossCompilationSymbols(
		string interfaceSource = "namespace Lib; public interface IFoo { void Do(); }",
		string typeName = "IFoo")
	{
		var libTree = CSharpSyntaxTree.ParseText(interfaceSource);
		var libCompilation = CSharpCompilation.Create(
			"Lib",
			[libTree],
			[MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		// Compile Lib to an in-memory stream so Consumer can reference it as metadata
		using var ms = new System.IO.MemoryStream();
		var emitResult = libCompilation.Emit(ms);
		Assert.True(emitResult.Success, string.Join("\n", emitResult.Diagnostics));
		ms.Position = 0;
		var libMetadataRef = MetadataReference.CreateFromStream(ms);

		var memberImpl = typeName == "IFoo" ? "public void Do() {}" : "";
		var consumerTree = CSharpSyntaxTree.ParseText(
			$"using Lib; public class Bar : {typeName} {{ {memberImpl} }}");
		var consumerCompilation = CSharpCompilation.Create(
			"Consumer",
			[consumerTree],
			[MetadataReference.CreateFromFile(typeof(object).Assembly.Location), libMetadataRef],
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		var sourceSymbol = libCompilation.GlobalNamespace
			.GetNamespaceMembers().First(n => n.Name == "Lib")
			.GetTypeMembers(typeName).Single();

		var metadataSymbol = consumerCompilation.GetTypeByMetadataName($"Lib.{typeName}")!;

		return (sourceSymbol, metadataSymbol);
	}

	[Fact]
	public void RoslynDefault_SameTypeFromDifferentCompilations_AreNotEqual()
	{
		// Documents the root cause: SymbolEqualityComparer.Default treats the same logical type
		// as different objects when retrieved from separate compilations.
		var (source, metadata) = BuildCrossCompilationSymbols();

		Assert.False(SymbolEqualityComparer.Default.Equals(source, metadata));
	}

	[Fact]
	public void NamedTypeSignatureComparer_SameTypeFromDifferentCompilations_AreEqual()
	{
		var (source, metadata) = BuildCrossCompilationSymbols();

		Assert.True(NamedTypeSignatureComparer.Instance.Equals(source, metadata));
	}

	[Fact]
	public void NamedTypeSignatureComparer_SameTypeFromDifferentCompilations_HaveSameHashCode()
	{
		var (source, metadata) = BuildCrossCompilationSymbols();

		Assert.Equal(
			NamedTypeSignatureComparer.Instance.GetHashCode(source),
			NamedTypeSignatureComparer.Instance.GetHashCode(metadata));
	}

	[Fact]
	public void NamedTypeSignatureComparer_DifferentTypes_AreNotEqual()
	{
		var (iFoo, _) = BuildCrossCompilationSymbols("namespace Lib; public interface IFoo { }", "IFoo");
		var (iBar, _) = BuildCrossCompilationSymbols("namespace Lib; public interface IBar { }", "IBar");

		Assert.False(NamedTypeSignatureComparer.Instance.Equals(iFoo, iBar));
	}

	[Fact]
	public void NamedTypeSignatureComparer_GenericTypeDistinguishedByArity()
	{
		var (nonGeneric, _) = BuildCrossCompilationSymbols("namespace Lib; public interface IFoo { }", "IFoo");
		var (generic, _) = BuildCrossCompilationSymbols("namespace Lib; public interface IFoo<T> { }", "IFoo");

		Assert.False(NamedTypeSignatureComparer.Instance.Equals(nonGeneric, generic));
	}

	[Fact]
	public void NamedTypeSignatureComparer_UsableAsDictionaryKey_FindsCrossCompilationSymbol()
	{
		var (source, metadata) = BuildCrossCompilationSymbols();

		// Simulate the inheritance map: keyed on the source symbol, looked up with metadata symbol
		var dict = new Dictionary<INamedTypeSymbol, string>(NamedTypeSignatureComparer.Instance)
		{
			[source] = "found"
		};

		Assert.True(dict.TryGetValue(metadata, out var value));
		Assert.Equal("found", value);
	}

	[Fact]
	public void SymbolSignatureComparer_SameTypeFromDifferentCompilations_AreEqual()
	{
		var (source, metadata) = BuildCrossCompilationSymbols();

		Assert.True(SymbolSignatureComparer.Instance.Equals(source, metadata));
	}

	[Fact]
	public void SymbolSignatureComparer_SameMethodFromDifferentCompilations_AreEqual()
	{
		var (source, metadata) = BuildCrossCompilationSymbols();

		var sourceMethod = source.GetMembers("Do").OfType<IMethodSymbol>().Single();
		var metadataMethod = metadata.GetMembers("Do").OfType<IMethodSymbol>().Single();

		Assert.True(SymbolSignatureComparer.Instance.Equals(sourceMethod, metadataMethod));
	}

	[Fact]
	public void SymbolSignatureComparer_UsableAsHashSetKey_FindsCrossCompilationSymbol()
	{
		var (source, metadata) = BuildCrossCompilationSymbols();

		// Simulate the referenced-symbol set: added from one compilation, checked from another
		var set = new HashSet<ISymbol>(SymbolSignatureComparer.Instance) { source };

		Assert.Contains(metadata, set);
	}
}
