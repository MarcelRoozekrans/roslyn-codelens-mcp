using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tests.Symbols;

public class SymbolSignatureComparerTests
{
	// No forwarding: these tests compare types of in-memory compilations, which forward nothing.
	private static readonly NamedTypeSignatureComparer TypeComparer = new(TypeForwardingMap.Empty);
	private static readonly SymbolSignatureComparer SymbolComparer = new(TypeComparer);

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

		Assert.True(TypeComparer.Equals(source, metadata));
	}

	[Fact]
	public void NamedTypeSignatureComparer_SameTypeFromDifferentCompilations_HaveSameHashCode()
	{
		var (source, metadata) = BuildCrossCompilationSymbols();

		Assert.Equal(
			TypeComparer.GetHashCode(source),
			TypeComparer.GetHashCode(metadata));
	}

	[Fact]
	public void NamedTypeSignatureComparer_DifferentTypes_AreNotEqual()
	{
		var (iFoo, _) = BuildCrossCompilationSymbols("namespace Lib; public interface IFoo { }", "IFoo");
		var (iBar, _) = BuildCrossCompilationSymbols("namespace Lib; public interface IBar { }", "IBar");

		Assert.False(TypeComparer.Equals(iFoo, iBar));
	}

	[Fact]
	public void NamedTypeSignatureComparer_GenericTypeDistinguishedByArity()
	{
		var (nonGeneric, _) = BuildCrossCompilationSymbols("namespace Lib; public interface IFoo { }", "IFoo");
		var (generic, _) = BuildCrossCompilationSymbols("namespace Lib; public interface IFoo<T> { }", "IFoo");

		Assert.False(TypeComparer.Equals(nonGeneric, generic));
	}

	[Fact]
	public void NamedTypeSignatureComparer_UsableAsDictionaryKey_FindsCrossCompilationSymbol()
	{
		var (source, metadata) = BuildCrossCompilationSymbols();

		// Simulate the inheritance map: keyed on the source symbol, looked up with metadata symbol
		var dict = new Dictionary<INamedTypeSymbol, string>(TypeComparer)
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

		Assert.True(SymbolComparer.Equals(source, metadata));
	}

	[Fact]
	public void SymbolSignatureComparer_SameMethodFromDifferentCompilations_AreEqual()
	{
		var (source, metadata) = BuildCrossCompilationSymbols();

		var sourceMethod = source.GetMembers("Do").OfType<IMethodSymbol>().Single();
		var metadataMethod = metadata.GetMembers("Do").OfType<IMethodSymbol>().Single();

		Assert.True(SymbolComparer.Equals(sourceMethod, metadataMethod));
	}

	[Fact]
	public void SymbolSignatureComparer_UsableAsHashSetKey_FindsCrossCompilationSymbol()
	{
		var (source, metadata) = BuildCrossCompilationSymbols();

		// Simulate the referenced-symbol set: added from one compilation, checked from another
		var set = new HashSet<ISymbol>(SymbolComparer) { source };

		Assert.Contains(metadata, set);
	}

	[Fact]
	public void SymbolSignatureComparer_SameMethodWithAndWithoutNullableAnnotations_AreEqual()
	{
		// A framework method is annotated in the net10 reference assemblies (Console.WriteLine(string? value)) but not
		// in net48's mscorlib (Console.WriteLine(string value)). C# cannot overload on nullable annotations, so both
		// are the same method.
		var annotated = CompileMethodOwner("#nullable enable\nnamespace Lib; public class C { public void M(string? s, System.Collections.Generic.List<string?> l) { } }");
		var oblivious = CompileMethodOwner("namespace Lib; public class C { public void M(string s, System.Collections.Generic.List<string> l) { } }");

		Assert.True(SymbolComparer.Equals(annotated, oblivious));
		Assert.Equal(SymbolComparer.GetHashCode(annotated), SymbolComparer.GetHashCode(oblivious));
	}

	private static IMethodSymbol CompileMethodOwner(string source)
	{
		var compilation = CSharpCompilation.Create(
			"Lib",
			[CSharpSyntaxTree.ParseText(source)],
			[MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		return compilation.GetTypeByMetadataName("Lib.C")!.GetMembers("M").OfType<IMethodSymbol>().Single();
	}

	// Compiles source as assembly "Lib" plus a consumer that references Lib as metadata, so every
	// declaration exists both as a source symbol and as a metadata symbol.
	private static (CSharpCompilation Lib, CSharpCompilation Consumer) CompileLibWithConsumer(string source)
	{
		var corlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
		var lib = CSharpCompilation.Create(
			"Lib",
			[CSharpSyntaxTree.ParseText(source)],
			[corlib],
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		using var ms = new System.IO.MemoryStream();
		var emitResult = lib.Emit(ms);
		Assert.True(emitResult.Success, string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString())));
		ms.Position = 0;

		var consumer = CSharpCompilation.Create(
			"Consumer",
			[],
			[corlib, MetadataReference.CreateFromStream(ms)],
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		return (lib, consumer);
	}

	[Fact]
	public void NamedTypeSignatureComparer_NestedAndTopLevelTypeWithSameName_AreNotEqual()
	{
		var (lib, _) = CompileLibWithConsumer(
			"namespace Lib; public class Widget { } public class Holder { public class Widget { } }");

		var topLevel = lib.GetTypeByMetadataName("Lib.Widget")!;
		var nested = lib.GetTypeByMetadataName("Lib.Holder+Widget")!;

		Assert.False(TypeComparer.Equals(topLevel, nested));
	}

	[Fact]
	public void NamedTypeSignatureComparer_NestedTypesWithSameNameInDifferentOuterTypes_AreNotEqual()
	{
		var (lib, _) = CompileLibWithConsumer(
			"namespace Lib; public class A { public class Widget { } } public class B { public class Widget { } }");

		var inA = lib.GetTypeByMetadataName("Lib.A+Widget")!;
		var inB = lib.GetTypeByMetadataName("Lib.B+Widget")!;

		Assert.False(TypeComparer.Equals(inA, inB));
	}

	[Fact]
	public void NamedTypeSignatureComparer_SameNestedTypeFromDifferentCompilations_AreEqualWithSameHashCode()
	{
		var (lib, consumer) = CompileLibWithConsumer(
			"namespace Lib; public class Holder { public class Widget { } }");

		var source = lib.GetTypeByMetadataName("Lib.Holder+Widget")!;
		var metadata = consumer.GetTypeByMetadataName("Lib.Holder+Widget")!;

		Assert.True(TypeComparer.Equals(source, metadata));
		Assert.Equal(
			TypeComparer.GetHashCode(source),
			TypeComparer.GetHashCode(metadata));
	}

	[Fact]
	public void SymbolSignatureComparer_MembersOfNestedTypesWithSameName_AreNotEqual()
	{
		var (lib, _) = CompileLibWithConsumer(
			"namespace Lib; public class A { public class Widget { public int Value; } } " +
			"public class B { public class Widget { public int Value; } }");

		var inA = lib.GetTypeByMetadataName("Lib.A+Widget")!.GetMembers("Value").Single();
		var inB = lib.GetTypeByMetadataName("Lib.B+Widget")!.GetMembers("Value").Single();

		Assert.False(SymbolComparer.Equals(inA, inB));
	}

	[Theory]
	[InlineData("ref")]
	[InlineData("out")]
	[InlineData("in")]
	public void SymbolSignatureComparer_OverloadsDifferingOnlyByRefKind_AreNotEqual(string modifier)
	{
		var body = string.Equals(modifier, "out", StringComparison.Ordinal) ? "value = 0;" : string.Empty;
		var (lib, _) = CompileLibWithConsumer(
			$"namespace Lib; public class C {{ public void M(int value) {{ }} public void M({modifier} int value) {{ {body} }} }}");

		var overloads = lib.GetTypeByMetadataName("Lib.C")!.GetMembers("M");

		Assert.Equal(2, overloads.Length);
		Assert.False(SymbolComparer.Equals(overloads[0], overloads[1]));
	}

	[Fact]
	public void SymbolSignatureComparer_OverloadsDifferingOnlyByArity_AreNotEqual()
	{
		var (lib, _) = CompileLibWithConsumer(
			"namespace Lib; public class C { public void M(int value) { } public void M<T>(int value) { } }");

		var overloads = lib.GetTypeByMetadataName("Lib.C")!.GetMembers("M");

		Assert.Equal(2, overloads.Length);
		Assert.False(SymbolComparer.Equals(overloads[0], overloads[1]));
	}

	[Fact]
	public void SymbolSignatureComparer_ConversionOperatorsDifferingOnlyByReturnType_AreNotEqual()
	{
		var (lib, _) = CompileLibWithConsumer(
			"namespace Lib; public class C { " +
			"public static implicit operator int(C c) => 0; public static implicit operator string(C c) => \"\"; }");

		var operators = lib.GetTypeByMetadataName("Lib.C")!.GetMembers("op_Implicit");

		Assert.Equal(2, operators.Length);
		Assert.False(SymbolComparer.Equals(operators[0], operators[1]));
	}

	[Fact]
	public void SymbolSignatureComparer_IndexerOverloads_AreNotEqual()
	{
		var (lib, _) = CompileLibWithConsumer(
			"namespace Lib; public class C { public int this[int index] => 0; public int this[string key] => 0; }");

		var indexers = lib.GetTypeByMetadataName("Lib.C")!.GetMembers().OfType<IPropertySymbol>().ToList();

		Assert.Equal(2, indexers.Count);
		Assert.False(SymbolComparer.Equals(indexers[0], indexers[1]));
	}

	[Fact]
	public void SymbolSignatureComparer_OverloadsFromDifferentCompilations_MatchTheirOwnCounterpart()
	{
		// Every overload must still find its counterpart in another compilation, and only that one.
		var (lib, consumer) = CompileLibWithConsumer(
			"namespace Lib; public class C { " +
			"public void M(int value) { } public void M(ref int value) { } public void M<T>(int value) { } " +
			"public static implicit operator int(C c) => 0; public static implicit operator string(C c) => \"\"; " +
			"public int this[int index] => 0; public int this[string key] => 0; " +
			"public class Nested { public int Value; } }");

		var sourceMembers = AllMembers(lib.GetTypeByMetadataName("Lib.C")!);
		var metadataMembers = AllMembers(consumer.GetTypeByMetadataName("Lib.C")!);

		Assert.All(sourceMembers, source =>
		{
			var match = Assert.Single(metadataMembers, m => SymbolComparer.Equals(source, m));
			Assert.Equal(SymbolComparer.GetHashCode(source), SymbolComparer.GetHashCode(match));
		});

		static List<ISymbol> AllMembers(INamedTypeSymbol type) =>
			[
				.. type.GetMembers().Where(m => m.Name is "M" or "op_Implicit" or "this[]"),
				.. type.GetTypeMembers("Nested").Single().GetMembers("Value"),
			];
	}

	[Fact]
	public void SymbolSignatureComparer_SignatureTypeForwardedToAnotherAssembly_MatchesWithSameHashCode()
	{
		// Models a framework type seen from two target frameworks: Lib is compiled against a "Core" that declares
		// N.Widget (like mscorlib for net48), while the consumer references a "Core" facade that forwards N.Widget
		// to "Impl" (like System.Runtime forwarding to System.Private.CoreLib). The same method must still match.
		var corlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
		var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
		const string widget = "namespace N { public class Widget { } }";

		var coreDeclaring = EmitReference(CSharpCompilation.Create("Core", [CSharpSyntaxTree.ParseText(widget)], [corlib], options));
		var impl = EmitReference(CSharpCompilation.Create("Impl", [CSharpSyntaxTree.ParseText(widget)], [corlib], options));
		var coreForwarding = EmitReference(CSharpCompilation.Create(
			"Core",
			[CSharpSyntaxTree.ParseText("[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(N.Widget))]")],
			[corlib, impl],
			options));

		var lib = CSharpCompilation.Create(
			"Lib",
			[CSharpSyntaxTree.ParseText(
				"namespace Lib; public class C { public void M(N.Widget widget) { } " +
				"public static implicit operator N.Widget(C c) => null!; }")],
			[corlib, coreDeclaring],
			options);
		var consumer = CSharpCompilation.Create("Consumer", [], [corlib, EmitReference(lib), coreForwarding, impl], options);

		var sourceMembers = Members(lib);
		var metadataMembers = Members(consumer);

		// Precondition: both views of the method really see N.Widget in different assemblies.
		Assert.Equal("Core", ((IMethodSymbol)sourceMembers[0]).Parameters[0].Type.ContainingAssembly.Name);
		Assert.Equal("Impl", ((IMethodSymbol)metadataMembers[0]).Parameters[0].Type.ContainingAssembly.Name);
		Assert.Equal("Core", ((IMethodSymbol)sourceMembers[1]).ReturnType.ContainingAssembly.Name);
		Assert.Equal("Impl", ((IMethodSymbol)metadataMembers[1]).ReturnType.ContainingAssembly.Name);
		Assert.All(sourceMembers.Zip(metadataMembers), pair =>
		{
			Assert.True(SymbolComparer.Equals(pair.First, pair.Second));
			Assert.Equal(SymbolComparer.GetHashCode(pair.First), SymbolComparer.GetHashCode(pair.Second));
		});

		static ISymbol[] Members(CSharpCompilation compilation)
		{
			var type = compilation.GetTypeByMetadataName("Lib.C")!;
			return [type.GetMembers("M").Single(), type.GetMembers("op_Implicit").Single()];
		}

		static MetadataReference EmitReference(CSharpCompilation compilation)
		{
			using var ms = new System.IO.MemoryStream();
			var emitResult = compilation.Emit(ms);
			Assert.True(emitResult.Success, string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString())));
			return MetadataReference.CreateFromImage(ms.ToArray());
		}
	}

	// Repro for issue #222: NoPiaMissingCanonicalTypeSymbol (an ErrorTypeSymbol) has a null
	// ContainingNamespace and surfaces from AllInterfaces when embedded-interop canonical
	// type lookup fails (e.g. WebView2 referenced from a WPF app). The comparer must not NRE
	// when such a symbol is hashed or compared.
	private static INamedTypeSymbol BuildErrorTypeWithoutNamespace()
	{
		var compilation = CSharpCompilation.Create(
			"Empty",
			[],
			[MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
		return compilation.CreateErrorTypeSymbol(container: null, name: "", arity: 0);
	}

	[Fact]
	public void NamedTypeSignatureComparer_GetHashCode_ErrorTypeWithNullNamespace_DoesNotThrow()
	{
		var errorType = BuildErrorTypeWithoutNamespace();
		// Sanity check: this is the precondition the regression depends on.
		Assert.Null(errorType.ContainingNamespace);

		var ex = Record.Exception(() => TypeComparer.GetHashCode(errorType));

		Assert.Null(ex);
	}

	[Fact]
	public void NamedTypeSignatureComparer_Equals_ErrorTypeWithNullNamespace_DoesNotThrow()
	{
		var errorType = BuildErrorTypeWithoutNamespace();

		var ex = Record.Exception(() => TypeComparer.Equals(errorType, errorType));

		Assert.Null(ex);
	}

	[Fact]
	public void NamedTypeSignatureComparer_UsableAsDictionaryKey_ErrorTypeWithNullNamespace_DoesNotThrow()
	{
		// Mirrors BuildInheritanceMaps: the dictionary insert triggers GetHashCode on the key.
		var errorType = BuildErrorTypeWithoutNamespace();
		var dict = new Dictionary<INamedTypeSymbol, string>(TypeComparer);

		var ex = Record.Exception(() => dict[errorType] = "x");

		Assert.Null(ex);
	}
}
