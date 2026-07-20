using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Analysis;

/// <summary>Outcome of Roslyn's own change-signature engine, with its verdict preserved.</summary>
public sealed record BridgeResult(bool Succeeded, Solution? UpdatedSolution, string? FailureMessage);

/// <summary>Describes one parameter of the target signature, in final order.</summary>
public sealed record DesiredParameter(
    IParameterSymbol? Existing,                 // null for an added parameter
    string? Name,
    string? TypeName,
    ITypeSymbol? Type,
    string? CallSiteValue,
    string? DefaultValue);

/// <summary>
/// The ONLY place that touches Roslyn's internal change-signature API. Everything reflective is
/// resolved once and cached; <see cref="Probe"/> reports anything missing so a Roslyn upgrade
/// fails loudly instead of degrading into partial work.
/// </summary>
public static class ChangeSignatureBridge
{
    private const string ChangeSignatureNamespace = "Microsoft.CodeAnalysis.ChangeSignature";

    private static readonly Lazy<Members> Cached = new(Members.Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Names of every required internal Roslyn member that failed to resolve. Empty means healthy.
    /// </summary>
    public static IReadOnlyList<string> Probe() => Cached.Value.Missing;

    public static async Task<BridgeResult> ChangeSignatureAsync(
        Document document,
        IMethodSymbol method,
        IReadOnlyList<DesiredParameter> updated,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(updated);

        var m = Cached.Value;
        if (m.Missing.Count > 0)
        {
            throw new McpToolException(
                ToolErrorCode.Internal,
                "Roslyn's internal change-signature API is not available in this build: "
                    + string.Join(", ", m.Missing),
                new { missing = m.Missing });
        }

        // 1. SemanticDocument for the declaring document.
        var semanticDocumentTask = (Task)Invoke(
            m.SemanticDocumentCreateAsync!, null, [document, ct], "SemanticDocument.CreateAsync")!;
        await semanticDocumentTask.ConfigureAwait(false);
        var semanticDocument = GetTaskResult(semanticDocumentTask, "SemanticDocument.CreateAsync");

        var isExtensionMethod = method.IsExtensionMethod;

        // 2. The original configuration, straight from the symbol's parameters.
        var originalConfiguration = CreateConfiguration(
            m,
            method.Parameters.Select(p => CreateExistingParameter(m, p)).ToArray(),
            isExtensionMethod);

        // 3. The updated configuration: survivors keep their symbol, new parameters are described.
        var updatedConfiguration = CreateConfiguration(
            m,
            updated.Select(p => p.Existing is not null
                ? CreateExistingParameter(m, p.Existing)
                : CreateAddedParameter(m, p)).ToArray(),
            isExtensionMethod);

        // 4-6. The change, the analyzed context, and the options that bypass the IDE dialog.
        var signatureChange = Construct(
            m.SignatureChangeCtor!, [originalConfiguration, updatedConfiguration], "SignatureChange..ctor");
        var context = Construct(
            m.SucceededContextCtor!,
            [semanticDocument, PositionForTypeBinding(document, method), method, originalConfiguration],
            "ChangeSignatureAnalysisSucceededContext..ctor");
        var options = Construct(
            m.OptionsResultCtor!, [signatureChange, false], "ChangeSignatureOptionsResult..ctor");

        // 7. The C# language service. Internal type, parameterless ctor.
        var service = CreateService(m);

        // 8. The solution-wide entry point. NOT ChangeSignature(...), which rewrites the
        //    declaration only and would leave every call site broken.
        var changeTask = (Task)Invoke(
            m.ChangeSignatureWithContextAsync!, service, [context, options, ct],
            "AbstractChangeSignatureService.ChangeSignatureWithContextAsync")!;
        await changeTask.ConfigureAwait(false);
        var result = GetTaskResult(changeTask, "AbstractChangeSignatureService.ChangeSignatureWithContextAsync");

        // 9. Roslyn's own verdict, preserved rather than reinterpreted.
        var succeeded = (bool)GetProperty(m.ResultSucceeded!, result, "ChangeSignatureResult.Succeeded")!;
        var updatedSolution = (Solution?)GetProperty(
            m.ResultUpdatedSolution!, result, "ChangeSignatureResult.UpdatedSolution");
        var confirmation = (string?)GetProperty(
            m.ResultConfirmationMessage!, result, "ChangeSignatureResult.ConfirmationMessage");
        var failureKind = GetProperty(
            m.ResultFailureKind!, result, "ChangeSignatureResult.ChangeSignatureFailureKind");

        var failureMessage = succeeded
            ? null
            : confirmation ?? (failureKind is null
                ? "Roslyn refused the signature change without reporting a reason."
                : $"Roslyn refused the signature change: {failureKind}.");

        return new BridgeResult(succeeded, succeeded ? updatedSolution : null, failureMessage);
    }

    /// <summary>
    /// Where Roslyn speculatively binds the type names of added parameters. The declaration's own
    /// position resolves `using`s and the enclosing namespace; 0 would bind at file scope.
    /// </summary>
    private static int PositionForTypeBinding(Document document, IMethodSymbol method)
    {
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            if (document.FilePath is not null
                && string.Equals(reference.SyntaxTree.FilePath, document.FilePath, StringComparison.Ordinal))
            {
                return reference.Span.Start;
            }
        }

        return 0;
    }

    private static object CreateService(Members m)
    {
        try
        {
            return Activator.CreateInstance(m.CSharpServiceType!, nonPublic: true)
                ?? throw new McpToolException(
                    ToolErrorCode.Internal,
                    "Could not construct CSharpChangeSignatureService.",
                    new { member = "CSharpChangeSignatureService..ctor" });
        }
        catch (Exception ex) when (ex is not McpToolException)
        {
            throw new McpToolException(
                ToolErrorCode.Internal,
                $"Could not construct CSharpChangeSignatureService: {ex.Message}",
                new { member = "CSharpChangeSignatureService..ctor" });
        }
    }

    private static object CreateExistingParameter(Members m, IParameterSymbol parameter)
        => Construct(m.ExistingParameterCtor!, [parameter], "ExistingParameter..ctor");

    private static object CreateAddedParameter(Members m, DesiredParameter p)
    {
        // No default value ⇒ every existing call site must pass something explicit.
        // A default value ⇒ existing call sites may omit the argument entirely.
        var hasDefault = !string.IsNullOrEmpty(p.DefaultValue);
        var callSiteKind = Enum.ToObject(
            m.CallSiteKindType!, hasDefault ? m.CallSiteKindOmitted : m.CallSiteKindValue);

        return Construct(
            m.AddedParameterCtor!,
            [
                p.Type,
                p.TypeName ?? p.Type?.ToDisplayString() ?? string.Empty,
                p.Name ?? string.Empty,
                callSiteKind,
                p.CallSiteValue ?? string.Empty,
                !hasDefault,
                p.DefaultValue ?? string.Empty,
                p.Type is not null,
            ],
            "AddedParameter..ctor");
    }

    /// <summary>
    /// ParameterConfiguration.Create derives the `this`/`params`/default-value split itself, so the
    /// bridge never has to model those slots.
    /// </summary>
    private static object CreateConfiguration(Members m, object[] parameters, bool isExtensionMethod)
    {
        var typed = Array.CreateInstance(m.ParameterType!, parameters.Length);
        Array.Copy(parameters, typed, parameters.Length);
        var immutable = Invoke(
            m.ImmutableArrayCreate!, null, [typed], "ImmutableArray.Create<Parameter>");

        return Invoke(
            m.ParameterConfigurationCreate!, null, [immutable, isExtensionMethod, 0],
            "ParameterConfiguration.Create")!;
    }

    private static object Construct(ConstructorInfo ctor, object?[] args, string member)
    {
        try
        {
            return ctor.Invoke(args);
        }
        catch (TargetInvocationException ex)
        {
            throw new McpToolException(
                ToolErrorCode.Internal,
                $"Roslyn's {member} rejected the arguments: {ex.InnerException?.Message ?? ex.Message}",
                new { member });
        }
    }

    private static object? Invoke(MethodInfo method, object? instance, object?[] args, string member)
    {
        try
        {
            return method.Invoke(instance, args);
        }
        catch (TargetInvocationException ex)
        {
            throw new McpToolException(
                ToolErrorCode.Internal,
                $"Roslyn's {member} failed: {ex.InnerException?.Message ?? ex.Message}",
                new { member });
        }
    }

    private static object? GetProperty(PropertyInfo property, object instance, string member)
    {
        try
        {
            return property.GetValue(instance);
        }
        catch (TargetInvocationException ex)
        {
            throw new McpToolException(
                ToolErrorCode.Internal,
                $"Reading {member} failed: {ex.InnerException?.Message ?? ex.Message}",
                new { member });
        }
    }

    private static object GetTaskResult(Task task, string member)
    {
        var resultProperty = task.GetType().GetProperty("Result")
            ?? throw new McpToolException(
                ToolErrorCode.Internal,
                $"{member} did not return a Task with a result.",
                new { member });

        return resultProperty.GetValue(task)
            ?? throw new McpToolException(
                ToolErrorCode.Internal, $"{member} returned null.", new { member });
    }

    /// <summary>
    /// Every reflective handle the bridge needs, resolved once. Anything that fails to resolve is
    /// recorded in <see cref="Missing"/> rather than throwing, so Probe can report the whole set.
    /// </summary>
    private sealed class Members
    {
        public List<string> Missing { get; } = [];

        public Type? ParameterType { get; private set; }
        public Type? CallSiteKindType { get; private set; }
        public Type? CSharpServiceType { get; private set; }
        public ConstructorInfo? ExistingParameterCtor { get; private set; }
        public ConstructorInfo? AddedParameterCtor { get; private set; }
        public ConstructorInfo? SignatureChangeCtor { get; private set; }
        public ConstructorInfo? OptionsResultCtor { get; private set; }
        public ConstructorInfo? SucceededContextCtor { get; private set; }
        public MethodInfo? ParameterConfigurationCreate { get; private set; }
        public MethodInfo? SemanticDocumentCreateAsync { get; private set; }
        public MethodInfo? ChangeSignatureWithContextAsync { get; private set; }
        public MethodInfo? ImmutableArrayCreate { get; private set; }
        public PropertyInfo? ResultSucceeded { get; private set; }
        public PropertyInfo? ResultUpdatedSolution { get; private set; }
        public PropertyInfo? ResultConfirmationMessage { get; private set; }
        public PropertyInfo? ResultFailureKind { get; private set; }
        public int CallSiteKindValue { get; private set; }
        public int CallSiteKindOmitted { get; private set; }

        public static Members Resolve()
        {
            var m = new Members();

            var features = m.LoadAssembly("Microsoft.CodeAnalysis.Features");
            var csharpFeatures = m.LoadAssembly("Microsoft.CodeAnalysis.CSharp.Features");
            var workspaces = typeof(Workspace).Assembly;
            if (features is null || csharpFeatures is null)
            {
                return m;
            }

            var parameterType = m.FindType(features, $"{ChangeSignatureNamespace}.Parameter");
            var existingParameter = m.FindType(features, $"{ChangeSignatureNamespace}.ExistingParameter");
            var addedParameter = m.FindType(features, $"{ChangeSignatureNamespace}.AddedParameter");
            var callSiteKind = m.FindType(features, $"{ChangeSignatureNamespace}.CallSiteKind");
            var parameterConfiguration = m.FindType(features, $"{ChangeSignatureNamespace}.ParameterConfiguration");
            var signatureChange = m.FindType(features, $"{ChangeSignatureNamespace}.SignatureChange");
            var optionsResult = m.FindType(features, $"{ChangeSignatureNamespace}.ChangeSignatureOptionsResult");
            var analyzedContext = m.FindType(features, $"{ChangeSignatureNamespace}.ChangeSignatureAnalyzedContext");
            var succeededContext = m.FindType(features, $"{ChangeSignatureNamespace}.ChangeSignatureAnalysisSucceededContext");
            var changeSignatureResult = m.FindType(features, $"{ChangeSignatureNamespace}.ChangeSignatureResult");
            var abstractService = m.FindType(features, $"{ChangeSignatureNamespace}.AbstractChangeSignatureService");
            var semanticDocument = m.FindType(workspaces, "Microsoft.CodeAnalysis.SemanticDocument");
            m.CSharpServiceType = m.FindType(
                csharpFeatures, "Microsoft.CodeAnalysis.CSharp.ChangeSignature.CSharpChangeSignatureService");

            m.ParameterType = parameterType;
            m.CallSiteKindType = callSiteKind;

            if (callSiteKind is not null)
            {
                m.CallSiteKindValue = m.EnumValue(callSiteKind, "Value");
                m.CallSiteKindOmitted = m.EnumValue(callSiteKind, "Omitted");
            }

            if (existingParameter is not null)
            {
                m.ExistingParameterCtor = m.FindCtor(
                    existingParameter, [typeof(IParameterSymbol)], "ExistingParameter..ctor(IParameterSymbol)");
            }

            if (addedParameter is not null && callSiteKind is not null)
            {
                m.AddedParameterCtor = m.FindCtor(
                    addedParameter,
                    [
                        typeof(ITypeSymbol), typeof(string), typeof(string), callSiteKind,
                        typeof(string), typeof(bool), typeof(string), typeof(bool),
                    ],
                    "AddedParameter..ctor(ITypeSymbol, string, string, CallSiteKind, string, bool, string, bool)");
            }

            if (parameterConfiguration is not null && parameterType is not null)
            {
                m.ParameterConfigurationCreate = m.FindMethod(
                    parameterConfiguration,
                    "Create",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    [typeof(ImmutableArray<>).MakeGenericType(parameterType), typeof(bool), typeof(int)],
                    "ParameterConfiguration.Create(ImmutableArray<Parameter>, bool, int)");

                m.ImmutableArrayCreate = ResolveImmutableArrayCreate(m, parameterType);
            }

            if (signatureChange is not null && parameterConfiguration is not null)
            {
                m.SignatureChangeCtor = m.FindCtor(
                    signatureChange, [parameterConfiguration, parameterConfiguration],
                    "SignatureChange..ctor(ParameterConfiguration, ParameterConfiguration)");
            }

            if (optionsResult is not null && signatureChange is not null)
            {
                m.OptionsResultCtor = m.FindCtor(
                    optionsResult, [signatureChange, typeof(bool)],
                    "ChangeSignatureOptionsResult..ctor(SignatureChange, bool)");
            }

            if (succeededContext is not null && semanticDocument is not null && parameterConfiguration is not null)
            {
                m.SucceededContextCtor = m.FindCtor(
                    succeededContext,
                    [semanticDocument, typeof(int), typeof(ISymbol), parameterConfiguration],
                    "ChangeSignatureAnalysisSucceededContext..ctor(SemanticDocument, int, ISymbol, ParameterConfiguration)");
            }

            if (semanticDocument is not null)
            {
                m.SemanticDocumentCreateAsync = m.FindMethod(
                    semanticDocument,
                    "CreateAsync",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    [typeof(Document), typeof(CancellationToken)],
                    "SemanticDocument.CreateAsync(Document, CancellationToken)");
            }

            if (abstractService is not null && analyzedContext is not null && optionsResult is not null)
            {
                // The solution-wide entry point. Deliberately NOT ChangeSignature(...), which
                // returns a SyntaxNode and rewrites the declaration only.
                m.ChangeSignatureWithContextAsync = m.FindMethod(
                    abstractService,
                    "ChangeSignatureWithContextAsync",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    [analyzedContext, optionsResult, typeof(CancellationToken)],
                    "AbstractChangeSignatureService.ChangeSignatureWithContextAsync(ChangeSignatureAnalyzedContext, ChangeSignatureOptionsResult, CancellationToken)");
            }

            if (changeSignatureResult is not null)
            {
                m.ResultSucceeded = m.FindProperty(changeSignatureResult, "Succeeded");
                m.ResultUpdatedSolution = m.FindProperty(changeSignatureResult, "UpdatedSolution");
                m.ResultConfirmationMessage = m.FindProperty(changeSignatureResult, "ConfirmationMessage");
                m.ResultFailureKind = m.FindProperty(changeSignatureResult, "ChangeSignatureFailureKind");
            }

            return m;
        }

        /// <summary>ImmutableArray.Create&lt;T&gt;(params T[]) closed over the internal Parameter type.</summary>
        private static MethodInfo? ResolveImmutableArrayCreate(Members m, Type parameterType)
        {
            const string Name = "ImmutableArray.Create<Parameter>(Parameter[])";
            var open = typeof(ImmutableArray).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, "Create", StringComparison.Ordinal)
                    && candidate.IsGenericMethodDefinition
                    && candidate.GetGenericArguments().Length == 1
                    && candidate.GetParameters() is [{ ParameterType.IsArray: true }]);

            if (open is null)
            {
                m.Missing.Add(Name);
                return null;
            }

            return open.MakeGenericMethod(parameterType);
        }

        private Assembly? LoadAssembly(string name)
        {
            try
            {
                return Assembly.Load(name);
            }
            catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException or FileLoadException)
            {
                Missing.Add($"assembly {name}");
                return null;
            }
        }

        private Type? FindType(Assembly assembly, string fullName)
        {
            var type = assembly.GetType(fullName, throwOnError: false);
            if (type is null)
            {
                Missing.Add(fullName);
            }

            return type;
        }

        private ConstructorInfo? FindCtor(Type type, Type[] parameterTypes, string display)
        {
            var ctor = type.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, parameterTypes, modifiers: null);
            if (ctor is null)
            {
                Missing.Add(display);
            }

            return ctor;
        }

        private MethodInfo? FindMethod(
            Type type, string name, BindingFlags flags, Type[] parameterTypes, string display)
        {
            var method = type.GetMethod(name, flags, binder: null, parameterTypes, modifiers: null);
            if (method is null)
            {
                Missing.Add(display);
            }

            return method;
        }

        private PropertyInfo? FindProperty(Type type, string name)
        {
            var property = type.GetProperty(
                name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property is null)
            {
                Missing.Add($"ChangeSignatureResult.{name}");
            }

            return property;
        }

        private int EnumValue(Type enumType, string name)
        {
            if (!Enum.IsDefined(enumType, name))
            {
                Missing.Add($"CallSiteKind.{name}");
                return 0;
            }

            return (int)Enum.Parse(enumType, name);
        }
    }
}
