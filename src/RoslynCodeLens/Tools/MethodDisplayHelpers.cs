using System.Globalization;
using System.Xml;
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

internal static class MethodDisplayHelpers
{
    internal static OverloadParameter BuildParameter(IParameterSymbol p)
    {
        var defaultText = p.HasExplicitDefaultValue
            ? FormatDefault(p.ExplicitDefaultValue, p.Type)
            : null;

        var modifier = p.RefKind switch
        {
            RefKind.Ref => "ref",
            RefKind.Out => "out",
            RefKind.In => "in",
            RefKind.RefReadOnlyParameter => "ref readonly",
            _ => string.Empty,
        };

        return new OverloadParameter(
            Name: p.Name,
            Type: p.Type.ToDisplayString(),
            IsOptional: p.IsOptional,
            DefaultValue: defaultText,
            IsParams: p.IsParams,
            Modifier: modifier);
    }

    internal static string AccessibilityToString(Accessibility a) => a switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedAndInternal => "private protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.Private => "private",
        _ => "internal",
    };

    internal static string? ExtractSummary(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return null;

        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var summary = doc.SelectSingleNode("//summary");
            var text = summary?.InnerText.Trim();
            if (string.IsNullOrEmpty(text)) return null;

            // InnerText flattens nested tags (e.g. <see cref="X"/>) to empty strings, leaving
            // double-spaces. Collapse whitespace runs to single spaces for clean rendering.
            return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string FormatDefault(object? value, ITypeSymbol type)
    {
        // Enum defaults: ExplicitDefaultValue returns the underlying integer (e.g. 0). Look
        // up the matching field on the enum so we render `MyEnum.Foo` instead of `0`.
        if (value is not null && type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType)
        {
            foreach (var member in enumType.GetMembers().OfType<IFieldSymbol>())
            {
                if (member.HasConstantValue && Equals(member.ConstantValue, value))
                    return $"{enumType.Name}.{member.Name}";
            }
        }

        return value switch
        {
            null => "null",
            string s => $"\"{s}\"",
            bool b => b ? "true" : "false",
            char c => $"'{c}'",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "null",
        };
    }
}
