using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class FindBreakingChangesLogic
{
    private static readonly JsonSerializerOptions JsonOpts = CreateJsonOptions();

    public static FindBreakingChangesResult Execute(LoadedSolution loaded, SymbolResolver source, string baselinePath)
    {
        var baseline = LoadBaseline(baselinePath);
        var current = GetPublicApiSurfaceLogic.Execute(loaded, source).Entries;
        return Diff(baseline, current);
    }

    /// <summary>
    /// Pure diff entry point — exposed internally so tests can pass in-memory baselines
    /// without writing and reloading JSON.
    /// </summary>
    internal static FindBreakingChangesResult Diff(
        IReadOnlyList<PublicApiEntry> baseline,
        IReadOnlyList<PublicApiEntry> current)
    {
        var baselineByName = baseline
            .GroupBy(e => e.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var currentByName = current
            .GroupBy(e => e.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        var changes = new List<BreakingChange>();

        foreach (var b in baselineByName.Values)
        {
            if (currentByName.TryGetValue(b.Name, out var c))
            {
                if (b.Kind != c.Kind)
                {
                    changes.Add(new BreakingChange(
                        Kind: BreakingChangeKind.KindChanged,
                        Severity: BreakingChangeSeverity.Breaking,
                        Name: b.Name,
                        EntityKind: c.Kind,
                        Project: c.Project,
                        FilePath: c.FilePath,
                        Line: c.Line,
                        Details: $"Kind changed: {b.Kind} → {c.Kind}"));
                }
                else if (b.Accessibility != c.Accessibility)
                {
                    if (b.Accessibility == PublicApiAccessibility.Public &&
                        c.Accessibility == PublicApiAccessibility.Protected)
                    {
                        changes.Add(new BreakingChange(
                            Kind: BreakingChangeKind.AccessibilityNarrowed,
                            Severity: BreakingChangeSeverity.Breaking,
                            Name: b.Name,
                            EntityKind: c.Kind,
                            Project: c.Project,
                            FilePath: c.FilePath,
                            Line: c.Line,
                            Details: "Accessibility narrowed: Public → Protected"));
                    }
                    else if (b.Accessibility == PublicApiAccessibility.Protected &&
                             c.Accessibility == PublicApiAccessibility.Public)
                    {
                        changes.Add(new BreakingChange(
                            Kind: BreakingChangeKind.AccessibilityWidened,
                            Severity: BreakingChangeSeverity.NonBreaking,
                            Name: b.Name,
                            EntityKind: c.Kind,
                            Project: c.Project,
                            FilePath: c.FilePath,
                            Line: c.Line,
                            Details: "Accessibility widened: Protected → Public"));
                    }
                }
            }
            else
            {
                changes.Add(new BreakingChange(
                    Kind: BreakingChangeKind.Removed,
                    Severity: BreakingChangeSeverity.Breaking,
                    Name: b.Name,
                    EntityKind: b.Kind,
                    Project: b.Project,
                    FilePath: b.FilePath,
                    Line: b.Line,
                    Details: $"{b.Kind} '{b.Name}' removed from {b.Project}"));
            }
        }

        foreach (var c in currentByName.Values)
        {
            if (!baselineByName.ContainsKey(c.Name))
            {
                changes.Add(new BreakingChange(
                    Kind: BreakingChangeKind.Added,
                    Severity: BreakingChangeSeverity.NonBreaking,
                    Name: c.Name,
                    EntityKind: c.Kind,
                    Project: c.Project,
                    FilePath: c.FilePath,
                    Line: c.Line,
                    Details: $"{c.Kind} '{c.Name}' added"));
            }
        }

        changes.Sort((a, b) =>
        {
            var bySeverity = ((int)a.Severity).CompareTo((int)b.Severity);
            if (bySeverity != 0) return bySeverity;
            return string.CompareOrdinal(a.Name, b.Name);
        });

        var byKind = changes
            .GroupBy(c => c.Kind.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var bySeverity = changes
            .GroupBy(c => c.Severity.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var summary = new BreakingChangesSummary(
            TotalChanges: changes.Count,
            ByKind: byKind,
            BySeverity: bySeverity);

        return new FindBreakingChangesResult(summary, changes);
    }

    private static IReadOnlyList<PublicApiEntry> LoadBaseline(string baselinePath)
    {
        if (!File.Exists(baselinePath))
            throw new McpToolException(
                ToolErrorCode.FileNotFound,
                $"Baseline not found: {baselinePath}",
                new { baselinePath });

        var ext = Path.GetExtension(baselinePath);
        if (string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
            return LoadBaselineFromJson(baselinePath);
        if (string.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase))
            return LoadBaselineFromAssembly(baselinePath);

        throw new McpToolException(
            ToolErrorCode.InvalidArgument,
            $"Unsupported baseline file extension: '{ext}'. Expected .json or .dll.",
            new { baselinePath, extension = ext });
    }

    private static IReadOnlyList<PublicApiEntry> LoadBaselineFromJson(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var result = JsonSerializer.Deserialize<GetPublicApiSurfaceResult>(json, JsonOpts);
            return result?.Entries ?? [];
        }
        catch (JsonException ex)
        {
            throw new McpToolException(
                ToolErrorCode.InvalidArgument,
                $"Baseline JSON is not a valid get_public_api_surface result: {ex.Message}",
                new { baselinePath = path, reason = ex.Message });
        }
    }

    private static IReadOnlyList<PublicApiEntry> LoadBaselineFromAssembly(string path)
    {
        var reference = MetadataReference.CreateFromFile(path);

        // Without BCL references, every signature mentioning a referenced type (string, int,
        // Task<T>, ...) renders as an error type and FQNs diverge from the source-walked
        // counterpart. Pull the runtime's trusted platform assemblies so signatures resolve.
        var bclRefs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location));

        var compilation = CSharpCompilation.Create(
            "baseline-extraction",
            references: bclRefs.Append(reference));

        if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
            throw new McpToolException(
                ToolErrorCode.InvalidArgument,
                $"Failed to load assembly: {path}",
                new { baselinePath = path, reason = "GetAssemblyOrModuleSymbol returned null" });

        var projectName = Path.GetFileNameWithoutExtension(path);
        return PublicApiSurfaceExtractor.Extract(assembly, projectName, requireSourceLocation: false);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        opts.Converters.Add(new JsonStringEnumConverter());
        return opts;
    }
}
