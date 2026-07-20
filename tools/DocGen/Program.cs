using System.ComponentModel;
using System.Reflection;
using System.Text;
using ModelContextProtocol.Server;
using RoslynCodeLens.Tools;

string outputDir = "docs/site/docs/tools";
string extrasDir = "docs/tool-extras";
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--output") outputDir = args[i + 1];
    if (args[i] == "--extras") extrasDir = args[i + 1];
}

var categoryMap = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["go_to_definition"]           = "navigation",
    ["search_symbols"]             = "navigation",
    ["find_references"]            = "navigation",
    ["find_callers"]               = "navigation",
    ["find_implementations"]       = "navigation",
    ["find_attribute_usages"]      = "navigation",
    ["resolve_stack_trace"]        = "navigation",
    ["get_symbol_context"]         = "analysis",
    ["get_type_overview"]          = "analysis",
    ["get_type_hierarchy"]         = "analysis",
    ["get_file_overview"]          = "analysis",
    ["analyze_method"]             = "analysis",
    ["get_exception_flow"]         = "analysis",
    ["find_throw_sites"]           = "analysis",
    ["find_catch_blocks"]          = "analysis",
    ["get_method_source"]          = "analysis",
    ["analyze_change_impact"]      = "analysis",
    ["analyze_data_flow"]          = "analysis",
    ["analyze_control_flow"]       = "analysis",
    ["get_diagnostics"]            = "diagnostics",
    ["get_code_fixes"]             = "diagnostics",
    ["get_code_actions"]           = "diagnostics",
    ["apply_code_action"]          = "diagnostics",
    ["rename_symbol"]              = "diagnostics",
    ["find_unused_symbols"]        = "code-quality",
    ["get_complexity_metrics"]     = "code-quality",
    ["find_naming_violations"]     = "code-quality",
    ["find_large_classes"]         = "code-quality",
    ["find_circular_dependencies"] = "code-quality",
    ["find_reflection_usage"]      = "code-quality",
    ["get_di_registrations"]       = "di-dependencies",
    ["get_nuget_dependencies"]     = "di-dependencies",
    ["get_project_dependencies"]   = "di-dependencies",
    ["get_source_generators"]      = "source-generators",
    ["get_generated_code"]         = "source-generators",
    ["inspect_external_assembly"]  = "external-assemblies",
    ["peek_il"]                    = "external-assemblies",
    ["list_solutions"]             = "solution-management",
    ["set_active_solution"]        = "solution-management",
    ["load_solution"]              = "solution-management",
    ["unload_solution"]            = "solution-management",
    ["rebuild_solution"]           = "solution-management",
};

var categoryMeta = new Dictionary<string, (string Label, int Position)>(StringComparer.Ordinal)
{
    ["navigation"]          = ("Navigation", 1),
    ["analysis"]            = ("Analysis", 2),
    ["diagnostics"]         = ("Diagnostics & Refactoring", 3),
    ["code-quality"]        = ("Code Quality", 4),
    ["di-dependencies"]     = ("DI & Dependencies", 5),
    ["source-generators"]   = ("Source Generators", 6),
    ["external-assemblies"] = ("External Assemblies", 7),
    ["solution-management"] = ("Solution Management", 8),
};

Directory.CreateDirectory(outputDir);
Directory.CreateDirectory(extrasDir);

// Emit a landing page so that links to /tools resolve
File.WriteAllText(
    Path.Combine(outputDir, "index.md"),
    """
    ---
    title: "Tool Reference"
    sidebar_label: "Tool Reference"
    slug: /tools
    ---

    # Tool Reference

    Browse the full set of roslyn-codelens-mcp tools, organized by category.

    """ + Environment.NewLine);

foreach (var (dir, (label, position)) in categoryMeta)
{
    var catDir = Path.Combine(outputDir, dir);
    Directory.CreateDirectory(catDir);
    File.WriteAllText(
        Path.Combine(catDir, "_category_.json"),
        $$"""{"label": "{{label}}", "position": {{position}}}""" + Environment.NewLine);
}

var assembly = typeof(FindReferencesTool).Assembly;
var toolTypes = assembly.GetTypes()
    .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null);

int count = 0;
var ctx = new NullabilityInfoContext();
foreach (var toolType in toolTypes)
{
    var methods = toolType
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

    foreach (var method in methods)
    {
        var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>()!;
        var toolName = toolAttr.Name ?? method.Name;
        var toolDesc = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
        var slug = toolName.Replace('_', '-');
        var category = categoryMap.TryGetValue(toolName, out var cat) ? cat : "uncategorized";

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"title: \"{toolName}\"");
        sb.AppendLine($"sidebar_label: \"{toolName}\"");
        sb.AppendLine($"description: \"{EscapeYaml(toolDesc)}\"");
        sb.AppendLine($"slug: /tools/{slug}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# `{toolName}`");
        sb.AppendLine();
        sb.AppendLine(EscapeMdx(toolDesc));
        sb.AppendLine();

        var userParams = method.GetParameters()
            .Where(p => p.GetCustomAttribute<DescriptionAttribute>() is not null)
            .ToList();

        if (userParams.Count > 0)
        {
            sb.AppendLine("## Parameters");
            sb.AppendLine();
            sb.AppendLine("| Parameter | Type | Required | Description |");
            sb.AppendLine("|-----------|------|:--------:|-------------|");
            foreach (var p in userParams)
            {
                var desc = p.GetCustomAttribute<DescriptionAttribute>()!.Description;
                var optional = p.HasDefaultValue || ctx.Create(p).WriteState == NullabilityState.Nullable;
                sb.AppendLine($"| `{p.Name}` | `{FormatType(p.ParameterType)}` | {(optional ? "" : "✓")} | {EscapeMdxTableCell(desc)} |");
            }
            sb.AppendLine();
        }

        var sidecarPath = Path.Combine(extrasDir, $"{slug}.extra.md");
        if (File.Exists(sidecarPath))
        {
            sb.AppendLine(File.ReadAllText(sidecarPath).Trim());
            sb.AppendLine();
        }

        Directory.CreateDirectory(Path.Combine(outputDir, category));
        var outPath = Path.Combine(outputDir, category, $"{slug}.md");
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine($"  {outPath}");
        count++;
    }
}

Console.WriteLine($"Done — {count} tool pages generated.");
return 0;

static string FormatType(Type t)
{
    var underlying = Nullable.GetUnderlyingType(t);
    if (underlying is not null) return $"{FormatType(underlying)}?";
    if (!t.IsGenericType) return t.Name switch
    {
        "String" => "string", "Int32" => "int", "Int64" => "long",
        "Boolean" => "bool",  "Double" => "double", "Void" => "void",
        _ => t.Name,
    };
    var baseName = t.GetGenericTypeDefinition().Name;
    baseName = baseName[..baseName.IndexOf('`', StringComparison.Ordinal)];
    var args = string.Join(", ", t.GetGenericArguments().Select(FormatType));
    return $"{baseName}<{args}>";
}

static string EscapeYaml(string s) =>
    s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "").Replace("\t", " ");

// MDX parses '<' and '{' outside inline code spans as JSX, so tool descriptions containing
// compiler-mangled names (<M>d__N.MoveNext, <>c) break the docs build. Backslash-escape them
// everywhere except inside backtick code spans, where MDX already treats them literally.
// If the backticks turn out unbalanced, the toggle would leave escaping disabled from the
// stray backtick onward — fall back to escaping ALL '<'/'{' regardless of code spans.
// (ToolDescriptionMdxSafetyTests is the authoring-time gate that keeps this path cold.)
static string EscapeMdx(string s)
{
    var sb = new StringBuilder(s.Length + 8);
    var inCode = false;
    foreach (var ch in s)
    {
        if (ch == '`') { inCode = !inCode; sb.Append(ch); continue; }
        if (!inCode && ch is '<' or '{') sb.Append('\\');
        sb.Append(ch);
    }
    if (!inCode) return sb.ToString();

    sb.Clear();
    foreach (var ch in s)
    {
        if (ch is '<' or '{') sb.Append('\\');
        sb.Append(ch);
    }
    return sb.ToString();
}

// Table cells additionally need every '|' escaped — markdown splits cells before inline
// parsing, so even pipes inside code spans would break the row.
static string EscapeMdxTableCell(string s) =>
    EscapeMdx(s).Replace("|", "\\|", StringComparison.Ordinal);
