namespace RoslynCodeLens.Models;

/// <summary>
/// One extension member applicable to a queried receiver type.
/// Kind: method | property. Signature is the REDUCED, call-site form — what a caller types
/// (<c>Where&lt;int&gt;(Func&lt;int, bool&gt;)</c>), not the declared form.
/// Namespace is always reported because applicability does not imply the <c>using</c> is present.
/// </summary>
/// <param name="IsStatic">
/// A C# 14 extension block may declare static members, and they are invoked on the TYPE
/// (<c>int.Zero</c>) rather than on an instance (<c>value.Zero</c>). Both are genuinely applicable
/// to the queried type, so both are reported — but a caller told only "property Zero applies to
/// int" would write the instance form and be wrong. This is the field that distinguishes them.
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
