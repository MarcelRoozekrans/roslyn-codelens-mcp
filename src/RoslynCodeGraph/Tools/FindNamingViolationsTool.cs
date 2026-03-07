using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class FindNamingViolationsLogic
{
    public static List<NamingViolation> Execute(LoadedSolution loaded, SymbolResolver resolver, string? project)
    {
        var results = new List<NamingViolation>();

        foreach (var type in resolver.AllTypes)
        {
            if (type.IsImplicitlyDeclared)
                continue;

            var projectName = resolver.GetProjectName(type);

            if (project != null &&
                !projectName.Contains(project, StringComparison.OrdinalIgnoreCase))
                continue;

            // Check interface naming
            if (type.TypeKind == TypeKind.Interface)
            {
                if (!type.Name.StartsWith("I") || type.Name.Length < 2 || !char.IsUpper(type.Name[1]))
                {
                    var (file, line) = resolver.GetFileAndLine(type);
                    if (!string.IsNullOrEmpty(file))
                    {
                        results.Add(new NamingViolation(
                            type.Name, "Interface", "Interfaces must start with 'I'",
                            $"I{type.Name}", file, line, projectName));
                    }
                }
            }
            else
            {
                // Check type naming (PascalCase)
                if (!string.IsNullOrEmpty(type.Name) && !type.Name.StartsWith(".") && !char.IsUpper(type.Name[0]))
                {
                    var (file, line) = resolver.GetFileAndLine(type);
                    if (!string.IsNullOrEmpty(file))
                    {
                        var suggestion = char.ToUpper(type.Name[0]) + type.Name.Substring(1);
                        results.Add(new NamingViolation(
                            type.Name, "Type", "Types must be PascalCase",
                            suggestion, file, line, projectName));
                    }
                }
            }

            // Check members
            foreach (var member in type.GetMembers())
            {
                if (member.IsImplicitlyDeclared)
                    continue;

                if (string.IsNullOrEmpty(member.Name) || member.Name.StartsWith("."))
                    continue;

                switch (member)
                {
                    case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary:
                        if (!char.IsUpper(method.Name[0]))
                        {
                            var (mf, ml) = resolver.GetFileAndLine(method);
                            if (!string.IsNullOrEmpty(mf))
                            {
                                var suggestion = char.ToUpper(method.Name[0]) + method.Name.Substring(1);
                                results.Add(new NamingViolation(
                                    method.Name, "Method", "Methods must be PascalCase",
                                    suggestion, mf, ml, projectName));
                            }
                        }

                        // Check parameters
                        foreach (var param in method.Parameters)
                        {
                            if (param.IsImplicitlyDeclared)
                                continue;

                            if (string.IsNullOrEmpty(param.Name) || param.Name.StartsWith("."))
                                continue;

                            if (char.IsUpper(param.Name[0]))
                            {
                                var (pf, pl) = resolver.GetFileAndLine(method);
                                if (!string.IsNullOrEmpty(pf))
                                {
                                    var paramSuggestion = char.ToLower(param.Name[0]) + param.Name.Substring(1);
                                    results.Add(new NamingViolation(
                                        param.Name, "Parameter", "Parameters should be camelCase",
                                        paramSuggestion, pf, pl, projectName));
                                }
                            }
                        }
                        break;

                    case IPropertySymbol property:
                        if (!char.IsUpper(property.Name[0]))
                        {
                            var (pf, pl) = resolver.GetFileAndLine(property);
                            if (!string.IsNullOrEmpty(pf))
                            {
                                var suggestion = char.ToUpper(property.Name[0]) + property.Name.Substring(1);
                                results.Add(new NamingViolation(
                                    property.Name, "Property", "Properties must be PascalCase",
                                    suggestion, pf, pl, projectName));
                            }
                        }
                        break;

                    case IFieldSymbol field when field.DeclaredAccessibility == Accessibility.Private:
                        if (!field.Name.StartsWith("_"))
                        {
                            var (ff, fl) = resolver.GetFileAndLine(field);
                            if (!string.IsNullOrEmpty(ff))
                            {
                                results.Add(new NamingViolation(
                                    field.Name, "Field", "Private fields should start with '_'",
                                    $"_{field.Name}", ff, fl, projectName));
                            }
                        }
                        break;
                }
            }
        }

        return results;
    }
}

[McpServerToolType]
public static class FindNamingViolationsTool
{
    [McpServerTool(Name = "find_naming_violations"),
     Description("Check .NET naming convention compliance: PascalCase types/methods/properties, camelCase parameters, I-prefix interfaces, _ prefix private fields")]
    public static List<NamingViolation> Execute(
        SolutionManager manager,
        [Description("Optional project name filter")] string? project = null)
    {
        manager.EnsureLoaded();
        return FindNamingViolationsLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), project);
    }
}
