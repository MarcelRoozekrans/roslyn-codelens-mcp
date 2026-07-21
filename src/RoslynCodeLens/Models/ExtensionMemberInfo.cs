namespace RoslynCodeLens.Models;

/// <summary>
/// One extension member applicable to a queried receiver type.
/// Kind: method | property. Signature is the REDUCED, call-site form — what a caller types
/// (<c>Where&lt;int&gt;(Func&lt;int, bool&gt;)</c>), not the declared form.
/// Namespace is always reported because applicability does not imply the <c>using</c> is present.
/// </summary>
public record ExtensionMemberInfo(
    string Name,
    string Kind,
    string Signature,
    string DeclaringType,
    string Namespace,
    string Origin,
    string? File,
    int? Line,
    string? XmlDocSummary);
