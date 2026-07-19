using System.Text;
using System.Text.RegularExpressions;

namespace RoslynCodeLens.StackTrace;

/// <summary>
/// Shared normalizer for runtime type names appearing in stack traces
/// (frame declaring types and exception header types).
/// </summary>
public static partial class TypeNameNormalizer
{
    [GeneratedRegex(@"`\d+")] private static partial Regex Arity();

    /// <summary>
    /// Display form: generic-instantiation blocks and backtick arity stripped,
    /// '+' nesting converted to '.'. Matches the resolver's arity-stripped index.
    /// </summary>
    public static string Normalize(string typeName)
        => StripArity(StripInstantiations(typeName)).Replace('+', '.');

    /// <summary>
    /// Removes generic-instantiation blocks ("[...]"/"[[...]]" suffixes) but keeps
    /// '+' nesting and backtick arity — the form GetTypeByMetadataName speaks.
    /// </summary>
    public static string StripInstantiations(string typeName)
    {
        if (!typeName.Contains('[')) return typeName;
        var sb = new StringBuilder(typeName.Length);
        var depth = 0;
        foreach (var c in typeName)
        {
            if (c == '[') depth++;
            else if (c == ']') { if (depth > 0) depth--; }
            else if (depth == 0) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Strips backtick arity markers (`N) only.</summary>
    public static string StripArity(string typeName) => Arity().Replace(typeName, "");

    /// <summary>
    /// Removes balanced angle-bracket blocks ("&lt;TKey, TValue&gt;"). Demystified traces
    /// print generic types in display form with type-argument blocks the arity-stripped
    /// source index does not carry.
    /// </summary>
    public static string StripAngleGenerics(string typeName)
    {
        if (!typeName.Contains('<')) return typeName;
        var sb = new StringBuilder(typeName.Length);
        var depth = 0;
        foreach (var c in typeName)
        {
            if (c == '<') depth++;
            else if (c == '>') { if (depth > 0) depth--; }
            else if (depth == 0) sb.Append(c);
        }
        return sb.ToString();
    }
}
