namespace RoslynCodeLens.Models;

/// <summary>
/// One extension member applicable to a queried receiver type.
/// Kind: method | property. Namespace is always reported because applicability does not imply the
/// <c>using</c> is present.
/// </summary>
/// <param name="Signature">
/// The REDUCED, call-site form — the member as seen from the receiver, with the receiver parameter
/// dropped and generic inference already applied — rendered return type first for both kinds:
/// <c>IEnumerable&lt;int&gt; Where&lt;int&gt;(Func&lt;int, bool&gt;)</c>, <c>int Tripled</c>.
/// <para>
/// It is a signature, not paste-ready source: parameters are named by type, and a partially
/// inferred generic keeps the type parameters the compiler still infers from the arguments
/// (<c>IEnumerable&lt;TResult&gt; Select&lt;int, TResult&gt;(Func&lt;int, TResult&gt;)</c> — the
/// <c>int</c> is what the receiver pinned, <c>TResult</c> comes from the lambda you pass).
/// </para>
/// </param>
/// <param name="IsStatic">
/// Whether the member is invoked on the TYPE (<c>int.Zero</c>) rather than on an instance
/// (<c>value.Doubled()</c>) — only a C# 14 extension block can declare such a member. It is NOT
/// "the declaration says static": every classic extension method is declared static and every one
/// of them is called on an instance. Both forms are genuinely applicable to the queried type, so
/// both are reported, and this is the field that tells them apart.
/// </param>
public record ExtensionMemberInfo(
    string Name,
    string Kind,
    string Signature,
    string DeclaringType,
    string Namespace,
    string Origin,
    bool IsStatic,
    string? File,
    int? Line,
    string? XmlDocSummary);
