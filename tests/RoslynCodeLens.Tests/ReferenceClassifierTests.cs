using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;

namespace RoslynCodeLens.Tests;

/// <summary>
/// The reference-kind taxonomy matrix. Each test compiles a tiny self-contained source,
/// binds every <see cref="SimpleNameSyntax"/> whose identifier matches the target, and
/// asserts the classifier's kind for those occurrences.
/// </summary>
public class ReferenceClassifierTests
{
    private static List<string> KindsOf(string source, string targetName, bool parseDocComments = false)
    {
        var parseOptions = parseDocComments
            ? new CSharpParseOptions(documentationMode: DocumentationMode.Parse)
            : new CSharpParseOptions(documentationMode: DocumentationMode.None);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var refs = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var comp = CSharpCompilation.Create("C", [tree], refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = comp.GetSemanticModel(tree);

        var kinds = new List<string>();
        foreach (var name in tree.GetRoot().DescendantNodes(descendIntoTrivia: true).OfType<SimpleNameSyntax>())
        {
            if (!string.Equals(name.Identifier.Text, targetName, StringComparison.Ordinal))
                continue;
            var info = model.GetSymbolInfo(name);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (symbol == null)
                continue;
            kinds.Add(ReferenceClassifier.Classify(name, symbol, () => model));
        }
        return kinds;
    }

    /// <summary>The single non-declaration occurrence — declarations are ambient noise in these fixtures.</summary>
    private static string OneKind(string source, string target)
        => Assert.Single(KindsOf(source, target), k => !string.Equals(k, "declaration", StringComparison.Ordinal));

    private const string Foo = "class Foo { } ";

    // ---- value references -------------------------------------------------

    [Fact]
    public void FieldRead_IsRead()
        => Assert.Equal(["read"], KindsOf("class C { int _x; int R() => _x; }", "_x"));

    [Fact]
    public void FieldSimpleAssignment_IsWrite()
        => Assert.Equal(["write"], KindsOf("class C { int _x; void W() { _x = 1; } }", "_x"));

    [Fact]
    public void FieldCompoundAssignment_IsReadWrite()
        => Assert.Equal(["readwrite"], KindsOf("class C { int _x; void W() { _x += 1; } }", "_x"));

    [Fact]
    public void FieldIncrementAndDecrement_AreReadWrite()
        => Assert.Equal(["readwrite", "readwrite"],
            KindsOf("class C { int _x; void W() { _x++; --_x; } }", "_x"));

    /// <summary>
    /// Assigning through an indexer mutates the referenced object's contents, not the
    /// reference: `_map` is never reassigned, so it reads. Keeps the receiver consistent
    /// with member access (`a.B = 5` reads `a`) and with the same mutation spelled
    /// `_map.Add(...)`, which is plainly a read of `_map` plus an invocation.
    /// </summary>
    [Fact]
    public void ElementAccessAssignment_ReadsTheReceiver()
        => Assert.Equal(["read"], KindsOf(
            "using System.Collections.Generic; " +
            "class C { Dictionary<int,int> _map = new(); void W() { _map[1] = 2; } }", "_map"));

    [Fact]
    public void ArrayElementAssignment_ReadsTheArray()
        => Assert.Equal(["read"], KindsOf("class C { int[] _a; void W() { _a[0] = 1; } }", "_a"));

    /// <summary>
    /// Tuple elements are ArgumentSyntax, so a deconstruction target would otherwise reach the
    /// by-value argument branch and report a read — hiding it from the mutation-site query.
    /// </summary>
    [Fact]
    public void DeconstructionAssignmentTarget_IsWrite()
        => Assert.Equal(["write"], KindsOf(
            "class C { int _x; void W() { int other; (_x, other) = (1, 2); } }", "_x"));

    [Fact]
    public void TupleUsedAsValue_StillReads()
        => Assert.Equal(["read"], KindsOf(
            "class C { int _x; (int, int) R() { return (_x, 0); } }", "_x"));

    [Fact]
    public void RefAliasInitializer_IsReadWrite()
        => Assert.Equal(["readwrite"], KindsOf(
            "class C { int _x; void W() { ref int r = ref _x; r++; } }", "_x"));

    [Fact]
    public void ConstructorInitializerCall_IsInvocation()
    {
        // `: this(0)` names its target with a keyword, so the classifier sees no SimpleName.
        const string source = "class C { public C() : this(0) { } public C(int x) { } }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var comp = CSharpCompilation.Create("C", [tree], refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = comp.GetSemanticModel(tree);
        var initializer = tree.GetRoot().DescendantNodes()
            .OfType<ConstructorInitializerSyntax>().Single();
        var target = model.GetSymbolInfo(initializer).Symbol!;

        Assert.Equal("invocation", ReferenceClassifier.Classify(initializer, target, () => model));
    }

    [Fact]
    public void OutArgument_IsWrite()
        => Assert.Equal(["write"],
            KindsOf("class C { void M(out int x) { x = 0; } void U() { int v; M(out v); } }", "v"));

    [Fact]
    public void RefArgument_IsReadWrite()
        => Assert.Equal(["readwrite"],
            KindsOf("class C { void M(ref int x) { } void U() { int v = 0; M(ref v); } }", "v"));

    [Fact]
    public void ByValueArgument_IsRead()
        => Assert.Equal(["read"],
            KindsOf("class C { void M(int x) { } void U() { int v = 0; M(v); } }", "v"));

    [Fact]
    public void NamedOutArgument_IsWrite()
        => Assert.Equal(["write"],
            KindsOf("class C { void M(int a, out int b) { b = 0; } void U() { int v; M(b: out v, a: 1); } }", "v"));

    [Fact]
    public void NamedByValueArgument_ReorderedBeforeRefParameter_IsRead()
        => Assert.Equal(["read"],
            KindsOf("class C { void M(ref int a, int b) { } void U() { int r = 0; int v = 0; M(b: v, a: ref r); } }", "v"));

    [Fact]
    public void MethodInvocation_IsInvocation()
        => Assert.Equal(["invocation"], KindsOf("class C { void M() { } void U() { M(); } }", "M"));

    [Fact]
    public void MethodInvocationThroughMemberAccess_IsInvocation()
        => Assert.Equal(["invocation"], KindsOf("class C { void M() { } void U() { this.M(); } }", "M"));

    [Fact]
    public void MethodInvocationThroughConditionalAccess_IsInvocation()
        => Assert.Equal(["invocation"],
            KindsOf("class C { public void M() { } static void U(C c) { c?.M(); } }", "M"));

    [Fact]
    public void MethodAsDelegateValue_IsMethodGroup()
        => Assert.Equal(["method_group"], KindsOf("class C { void H() { } System.Action U() => H; }", "H"));

    [Fact]
    public void PropertyThroughMemberAccessAssignment_IsWrite()
        => Assert.Equal(["write"],
            KindsOf("class C { public int P { get; set; } static void U(C c) { c.P = 1; } }", "P"));

    // ---- type references --------------------------------------------------

    [Fact]
    public void ObjectCreation_IsObjectCreation()
        => Assert.Equal("object_creation", OneKind(Foo + "class C { Foo U() => new Foo(); }", "Foo"));

    [Fact]
    public void ConstructorSymbol_IsObjectCreation()
    {
        const string source = Foo + "class C { Foo U() => new Foo(); }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var comp = CSharpCompilation.Create("C", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = comp.GetSemanticModel(tree);
        var creation = tree.GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Single();
        var ctor = (IMethodSymbol)model.GetSymbolInfo(creation).Symbol!;

        Assert.Equal(MethodKind.Constructor, ctor.MethodKind);
        Assert.Equal("object_creation", ReferenceClassifier.Classify(creation.Type, ctor, () => model));
    }

    [Fact]
    public void ExplicitCast_IsCast()
        => Assert.Equal("cast", OneKind(Foo + "class C { void U(object o) { var f = (Foo)o; } }", "Foo"));

    [Fact]
    public void AsCast_IsCast()
        => Assert.Equal("cast", OneKind(Foo + "class C { void U(object o) { var f = o as Foo; } }", "Foo"));

    [Fact]
    public void IsExpression_IsTypeCheck()
        => Assert.Equal("type_check", OneKind(Foo + "class C { bool U(object o) => o is Foo; }", "Foo"));

    [Fact]
    public void DeclarationPattern_IsTypeCheck()
        => Assert.Equal("type_check",
            OneKind(Foo + "class C { void U(object o) { if (o is Foo f) { } } }", "Foo"));

    [Fact]
    public void SwitchCasePattern_IsTypeCheck()
        => Assert.Equal("type_check",
            OneKind(Foo + "class C { void U(object o) { switch (o) { case Foo f: break; } } }", "Foo"));

    [Fact]
    public void TypeOf_IsTypeOf()
        => Assert.Equal("typeof", OneKind(Foo + "class C { void U() { var t = typeof(Foo); } }", "Foo"));

    [Fact]
    public void BaseType_IsBaseType()
        => Assert.Equal("base_type", OneKind(Foo + "class Bar : Foo { }", "Foo"));

    [Fact]
    public void TypeConstraint_IsTypeConstraint()
        => Assert.Equal("type_constraint", OneKind(Foo + "class C<T> where T : Foo { }", "Foo"));

    [Fact]
    public void TypeArgument_IsTypeArgument()
        => Assert.Equal("type_argument",
            OneKind(Foo + "class C { void U() { var l = new System.Collections.Generic.List<Foo>(); } }", "Foo"));

    [Fact]
    public void TypePositions_AreDeclaration()
        => Assert.Equal(["declaration", "declaration", "declaration"],
            KindsOf(Foo + "class C { Foo _f; void M(Foo p) { Foo local; } }", "Foo"));

    [Fact]
    public void Attribute_IsAttribute()
        => Assert.Equal("attribute",
            OneKind("class FooAttribute : System.Attribute { } [Foo] class C { }", "Foo"));

    // ---- any symbol -------------------------------------------------------

    [Fact]
    public void NameOf_IsNameOf()
        => Assert.Equal(["nameof"], KindsOf("class C { int _x; string N() => nameof(_x); }", "_x"));

    [Fact]
    public void NameOfType_IsNameOf()
        => Assert.Equal("nameof", OneKind(Foo + "class C { string N() => nameof(Foo); }", "Foo"));

    [Fact]
    public void XmlDocCref_IsXmlDoc()
        => Assert.Equal(["xml_doc"], KindsOf(
            """
            class C
            {
                void M() { }

                /// <see cref="M"/>
                void N() { }
            }
            """, "M", parseDocComments: true));
}
