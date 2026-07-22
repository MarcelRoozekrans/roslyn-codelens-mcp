using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetInstantiationOptionsTool
{
    [McpServerTool(Name = "get_instantiation_options")]
    [Description(
        "Answer 'how do I construct this type?' in one call — accessible constructors, static " +
        "factory methods declared anywhere in the solution, and DI registrations. " +
        "Constructors report every parameter's type and name, declared accessibility, whether the " +
        "constructor is compiler-supplied (`isImplicit` — a struct or a class with no declared " +
        "constructor still has a usable parameterless one), and whether it is obsolete. " +
        "Factories are static members returning the type, including ones declared on a DIFFERENT " +
        "type such as `WidgetFactory.Create()`, with `Task<T>`/`ValueTask<T>` unwrapped and " +
        "flagged `isAsync`. Instance builder methods are excluded because the builder itself " +
        "would need constructing. " +
        "Pass `fromProject` to get `accessible` computed for that project, which honours " +
        "`InternalsVisibleTo` — this is how you find out whether your test project can reach an " +
        "internal constructor. Without it, `accessible` is null, meaning NOT COMPUTED — which is " +
        "not the same as false, so do not read a null as 'you cannot call this'. " +
        "`requiredMembers` lists members that must be set in an object initializer. " +
        "`diRegistrations` shows where the type is registered in a container — a registered type " +
        "is usually meant to be resolved rather than constructed by hand. " +
        "For interfaces, abstract classes and static classes, `instantiable` is false and `note` " +
        "explains why; use `find_implementations` to find concrete types.")]
    public static InstantiationOptionsResult Execute(
        MultiSolutionManager manager,
        [Description("Type to construct — simple (`Widget`) or fully qualified (`MyApp.Widget`).")]
            string symbol,
        [Description("Optional project name whose viewpoint decides `accessible` (e.g. `MyApp.Tests`).")]
            string? fromProject = null,
        CancellationToken cancellationToken = default)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return GetInstantiationOptionsLogic.Execute(
            context.Loaded, context.Resolver, symbol, fromProject, cancellationToken);
    }
}
